using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Dapper;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using Telegram.Bot.Types.Enums;

namespace TelegramDoctorBot
{
    class Program
    {
        private static TelegramBotClient? botClient;
        private static Dictionary<long, UserState> userStates = new Dictionary<long, UserState>();
//твой строка подключение
        private static string connectionString = "Data Source=localhost;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True";
//__________________________________________________________________________________________________________________________________
        static async Task Main(string[] args)
        {
//твой токен
            botClient = new TelegramBotClient("токен");
 //__________________________________________________________________________________________________________________________________

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: CancellationToken.None
            );

            var me = await botClient.GetMeAsync();
            Console.WriteLine($"Bot started: {me.Username}");

            Console.WriteLine("Press any key to exit");
            Console.ReadKey();
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message)
                return;

            if (message.Text is not { } messageText)
                return;

            var chatId = message.Chat.Id;

            if (!userStates.ContainsKey(chatId))
            {
                userStates[chatId] = new UserState { CurrentStep = Step.Start };
            }

            var userState = userStates[chatId];

            try
            {
                switch (userState.CurrentStep)
                {
                    case Step.Start:
                        await botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: "Введите ваш СНИЛС (в формате XXX-XXX-XXX XX):",
                            cancellationToken: cancellationToken);
                        userState.CurrentStep = Step.EnterSnils;
                        break;

                    case Step.EnterSnils:
                        if (IsValidSnils(messageText))
                        {
                            var patient = await GetPatientBySnils(messageText);
                            if (patient != null)
                            {
                                userState.PatientId = patient.ID;
                                userState.Snils = messageText;
                                await ShowSpecialties(chatId, cancellationToken);
                                userState.CurrentStep = Step.SelectSpecialty;
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Пациент с таким СНИЛС не найден. Попробуйте еще раз:",
                                    cancellationToken: cancellationToken);
                            }
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат СНИЛС. Введите в формате XXX-XXX-XXX XX:",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case Step.SelectSpecialty:
                        var specialties = await GetSpecialties();
                        if (specialties.Contains(messageText))
                        {
                            userState.Specialty = messageText;
                            await ShowDoctors(chatId, messageText, cancellationToken);
                            userState.CurrentStep = Step.SelectDoctor;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Пожалуйста, выберите специальность из списка:",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case Step.SelectDoctor:
                        var doctors = await GetDoctorsBySpecialty(userState.Specialty);
                        var doctor = doctors.FirstOrDefault(d => d.FIO == messageText);
                        if (doctor != null)
                        {
                            userState.DoctorId = doctor.UserID;
                            await ShowAvailableDates(chatId, doctor.UserID, cancellationToken);
                            userState.CurrentStep = Step.SelectDate;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Пожалуйста, выберите врача из списка:",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case Step.SelectDate:
                        if (DateTime.TryParse(messageText, out var selectedDate))
                        {
                            userState.SelectedDate = selectedDate;
                            await ShowAvailableTimes(chatId, userState.DoctorId, selectedDate, cancellationToken);
                            userState.CurrentStep = Step.SelectTime;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат даты. Пожалуйста, введите дату в формате ДД.ММ.ГГГГ:",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case Step.SelectTime:
                        if (TimeSpan.TryParse(messageText, out var selectedTime))
                        {
                            userState.VisitDateTime = userState.SelectedDate.Date.Add(selectedTime);
                            if (await IsTimeSlotAvailable(userState.DoctorId, userState.VisitDateTime))
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Введите примечание к записи (или нажмите /skip чтобы пропустить):",
                                    cancellationToken: cancellationToken);
                                userState.CurrentStep = Step.EnterNotes;
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Это время уже занято. Пожалуйста, выберите другое время:",
                                    cancellationToken: cancellationToken);
                            }
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат времени. Пожалуйста, выберите время из списка:",
                                cancellationToken: cancellationToken);
                        }
                        break;

                    case Step.EnterNotes:
                        // Если пользователь ввел /skip, оставляем Notes пустым
                        userState.Notes = messageText.Equals("/skip", StringComparison.OrdinalIgnoreCase) 
                            ? null 
                            : messageText;

                        await CreateVisit(
                            userState.PatientId, 
                            userState.DoctorId, 
                            userState.VisitDateTime,
                            userState.Notes);

                        await botClient.SendTextMessageAsync(
                            chatId: chatId,
                            text: $"Вы успешно записаны на {userState.VisitDateTime:dd.MM.yyyy} в {userState.VisitDateTime:HH:mm}!\n" +
                                 $"Примечание: {(userState.Notes ?? "не указано")}",
                            cancellationToken: cancellationToken);
                        
                        userStates.Remove(chatId);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Произошла ошибка. Пожалуйста, начните заново.",
                    cancellationToken: cancellationToken);
                userStates.Remove(chatId);
            }
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Error: {exception.Message}");
            return Task.CompletedTask;
        }

        private static bool IsValidSnils(string snils)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(snils, @"^\d{3}-\d{3}-\d{3} \d{2}$");
        }

        private static async Task<Person?> GetPatientBySnils(string snils)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                return await connection.QueryFirstOrDefaultAsync<Person>(
                    "SELECT * FROM Persons WHERE SNILS = @Snils", new { Snils = snils });
            }
        }

        private static async Task<List<string>> GetSpecialties()
        {
            using (var connection = new SqlConnection(connectionString))
            {
                var specialties = await connection.QueryAsync<string>(
                    "SELECT DISTINCT Speciality FROM UserCredentialsDB WHERE Speciality IS NOT NULL");
                return specialties.ToList();
            }
        }

        private static async Task ShowSpecialties(long chatId, CancellationToken cancellationToken)
        {
            var specialties = await GetSpecialties();
            var keyboard = new ReplyKeyboardMarkup(
                specialties.Select(s => new KeyboardButton(s)).Chunk(2).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient!.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите специальность врача:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task<List<Doctor>> GetDoctorsBySpecialty(string specialty)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                return (await connection.QueryAsync<Doctor>(
                    "SELECT UserID, FIO FROM UserCredentialsDB WHERE Speciality = @Specialty", 
                    new { Specialty = specialty })).ToList();
            }
        }

        private static async Task ShowDoctors(long chatId, string specialty, CancellationToken cancellationToken)
        {
            var doctors = await GetDoctorsBySpecialty(specialty);
            var keyboard = new ReplyKeyboardMarkup(
                doctors.Select(d => new KeyboardButton(d.FIO)).Chunk(2).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient!.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите врача:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task ShowAvailableDates(long chatId, int doctorId, CancellationToken cancellationToken)
        {
            var availableDates = new List<DateTime>();
            for (int i = 0; i < 14; i++) // 2 недели вперед
            {
                var date = DateTime.Today.AddDays(i + 1); // Начиная с завтра
                if (date.DayOfWeek != DayOfWeek.Sunday && date.DayOfWeek != DayOfWeek.Saturday)
                {
                    availableDates.Add(date);
                }
            }

            var keyboard = new ReplyKeyboardMarkup(
                availableDates.Select(d => new KeyboardButton(d.ToString("dd.MM.yyyy"))).Chunk(3).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient!.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите дату:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task ShowAvailableTimes(long chatId, int doctorId, DateTime date, CancellationToken cancellationToken)
        {
            var bookedTimes = await GetBookedTimes(doctorId, date);
            var availableTimes = new List<string>();

            var startTime = new TimeSpan(8, 0, 0);
            var endTime = new TimeSpan(18, 0, 0);
            var lunchStart = new TimeSpan(13, 0, 0);
            var lunchEnd = new TimeSpan(14, 0, 0);

            for (var time = startTime; time < endTime; time = time.Add(new TimeSpan(0, 30, 0)))
            {
                if (time >= lunchStart && time < lunchEnd) continue; // Пропускаем обед

                var timeStr = time.ToString(@"hh\:mm");
                if (!bookedTimes.Contains(timeStr))
                {
                    availableTimes.Add(timeStr);
                }
            }

            var keyboard = new ReplyKeyboardMarkup(
                availableTimes.Select(t => new KeyboardButton(t)).Chunk(3).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient!.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите время:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }

        private static async Task<List<string>> GetBookedTimes(int doctorId, DateTime date)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                var times = await connection.QueryAsync<string>(
                    "SELECT CONVERT(VARCHAR(5), VisitDateTime, 108) FROM Visits " +
                    "WHERE EmployeeID = @DoctorId AND CONVERT(DATE, VisitDateTime) = @Date",
                    new { DoctorId = doctorId, Date = date.Date });
                return times.ToList();
            }
        }

        private static async Task<bool> IsTimeSlotAvailable(int doctorId, DateTime dateTime)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                var count = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM Visits WHERE EmployeeID = @DoctorId AND VisitDateTime = @DateTime",
                    new { DoctorId = doctorId, DateTime = dateTime });
                return count == 0;
            }
        }

        private static async Task CreateVisit(int patientId, int doctorId, DateTime visitDateTime, string? notes)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO Visits (PatientID, EmployeeID, VisitDateTime, Notes) " +
                    "VALUES (@PatientId, @DoctorId, @VisitDateTime, @Notes)",
                    new { 
                        PatientId = patientId, 
                        DoctorId = doctorId, 
                        VisitDateTime = visitDateTime,
                        Notes = notes
                    });
            }
        }
    }

    public class UserState
    {
        public Step CurrentStep { get; set; }
        public int PatientId { get; set; }
        public string Snils { get; set; } = null!;
        public string Specialty { get; set; } = null!;
        public int DoctorId { get; set; }
        public DateTime SelectedDate { get; set; }
        public DateTime VisitDateTime { get; set; }
        public string? Notes { get; set; }
    }

    public enum Step
    {
        Start,
        EnterSnils,
        SelectSpecialty,
        SelectDoctor,
        SelectDate,
        SelectTime,
        EnterNotes
    }

    public class Person
    {
        public int ID { get; set; }
        public string FirstName { get; set; } = null!;
        public string LastName { get; set; } = null!;
        public string? MiddleName { get; set; }
        public DateTime BirthDate { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Gender { get; set; }
        public string? SNILS { get; set; }
        public string RegistrationAddress { get; set; } = null!;
        public string? ActualAddress { get; set; }
    }

    public class Doctor
    {
        public int UserID { get; set; }
        public string FIO { get; set; } = null!;
    }
}