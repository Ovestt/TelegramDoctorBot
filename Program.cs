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
        private static TelegramBotClient botClient;
        private static Dictionary<long, UserState> userStates = new Dictionary<long, UserState>();
        private static Dictionary<long, long> chatIdToPatientIdMap = new Dictionary<long, long>();
        private static Timer notificationTimer;

        private static string connectionString = "база";

        static async Task Main(string[] args)
        {
            botClient = new TelegramBotClient("токен");

            notificationTimer = new Timer(CheckCompletedVisits, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

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

            notificationTimer?.Dispose();
        }

        private static async void CheckCompletedVisits(object state)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    var completedVisits = await connection.QueryAsync<CompletedVisit>(
                        @"SELECT v.VisitID, v.PatientID, v.EmployeeID, v.VisitDateTime, v.EndDateTime, v.Notes, 
                                 p.FirstName, p.LastName, p.MiddleName, u.FIO as DoctorName
                          FROM Visits v
                          JOIN Persons p ON v.PatientID = p.ID
                          JOIN UserCredentialsDB u ON v.EmployeeID = u.UserID
                          WHERE v.EndDateTime IS NOT NULL 
                          AND v.NotificationSent = 0");

                    foreach (var visit in completedVisits)
                    {
                        var chatIdEntry = chatIdToPatientIdMap.FirstOrDefault(x => x.Value == visit.PatientID);
                        if (chatIdEntry.Key != 0)
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatIdEntry.Key,
                                text: $"Ваш визит к врачу {visit.DoctorName} завершен.\n" +
                                      $"Дата: {visit.VisitDateTime:dd.MM.yyyy}\n" +
                                      $"Время приема: {visit.VisitDateTime:HH:mm}-{visit.EndDateTime:HH:mm}\n" +
                                      $"Примечание: {(visit.Notes ?? "не указано")}");
                            var diagnoses = await connection.QueryAsync<Diagnosis>(
                                "SELECT DiagnosisName, Description, DateCreated FROM Diagnoses " +
                                "WHERE PatientID = @PatientID AND DoctorUserID = @DoctorID " +
                                "AND DateCreated BETWEEN @VisitStart AND @VisitEnd",
                                new
                                {
                                    PatientID = visit.PatientID,
                                    DoctorID = visit.EmployeeID,
                                    VisitStart = visit.VisitDateTime,
                                    VisitEnd = visit.EndDateTime
                                });

                            if (diagnoses.Any())
                            {
                                var diagnosisMessage = "Поставленные диагнозы:\n" +
                                    string.Join("\n\n", diagnoses.Select(d =>
                                        $"• {d.DiagnosisName}\nОписание: {d.Description ?? "нет описания"}\nДата: {d.DateCreated:dd.MM.yyyy}"));

                                await botClient.SendTextMessageAsync(
                                    chatId: chatIdEntry.Key,
                                    text: diagnosisMessage);
                            }

                            var referrals = await connection.QueryAsync<MedicalReferral>(
                                "SELECT ReferralNumber, Purpose, Speciality, ServiceType, ReferralDate " +
                                "FROM MedicalReferrals " +
                                "WHERE PatientID = @PatientID AND DoctorUserID = @DoctorID " +
                                "AND ReferralDate BETWEEN @VisitStart AND @VisitEnd",
                                new
                                {
                                    PatientID = visit.PatientID,
                                    DoctorID = visit.EmployeeID,
                                    VisitStart = visit.VisitDateTime,
                                    VisitEnd = visit.EndDateTime
                                });

                            if (referrals.Any())
                            {
                                var referralMessage = "Выданные направления:\n" +
                                    string.Join("\n\n", referrals.Select(r =>
                                        $"• Номер: {r.ReferralNumber}\nЦель: {r.Purpose}\n" +
                                        $"Специальность: {r.Speciality ?? "не указана"}\n" +
                                        $"Тип услуги: {r.ServiceType}\nДата: {r.ReferralDate:dd.MM.yyyy}"));

                                await botClient.SendTextMessageAsync(
                                    chatId: chatIdEntry.Key,
                                    text: referralMessage);
                            }

                            var prescriptions = await connection.QueryAsync<Prescription>(
                                "SELECT MedicationName, Dosage, IssueDate, ExpiryDate " +
                                "FROM Prescriptions " +
                                "WHERE PatientID = @PatientID AND DoctorUserID = @DoctorID " +
                                "AND IssueDate BETWEEN @VisitStart AND @VisitEnd",
                                new
                                {
                                    PatientID = visit.PatientID,
                                    DoctorID = visit.EmployeeID,
                                    VisitStart = visit.VisitDateTime,
                                    VisitEnd = visit.EndDateTime
                                });

                            if (prescriptions.Any())
                            {
                                var prescriptionMessage = "Выписанные рецепты:\n" +
                                    string.Join("\n\n", prescriptions.Select(p =>
                                        $"• Лекарство: {p.MedicationName}\nДозировка: {p.Dosage}\n" +
                                        $"Дата выдачи: {p.IssueDate:dd.MM.yyyy}\n" +
                                        $"Срок действия: {(p.ExpiryDate.HasValue ? p.ExpiryDate.Value.ToString("dd.MM.yyyy") : "не указан")}"));

                                await botClient.SendTextMessageAsync(
                                    chatId: chatIdEntry.Key,
                                    text: prescriptionMessage);
                            }

                            var sickLeaves = await connection.QueryAsync<SickLeave>(
                                "SELECT Number, Diagnosis, StartDate, EndDate, Status, Type " +
                                "FROM SickLeaves " +
                                "WHERE PatientID = @PatientID AND DoctorUserID = @DoctorID " +
                                "AND IssueDate BETWEEN @VisitStart AND @VisitEnd",
                                new
                                {
                                    PatientID = visit.PatientID,
                                    DoctorID = visit.EmployeeID,
                                    VisitStart = visit.VisitDateTime,
                                    VisitEnd = visit.EndDateTime
                                });

                            if (sickLeaves.Any())
                            {
                                var sickLeaveMessage = "Оформленные больничные листы:\n" +
                                    string.Join("\n\n", sickLeaves.Select(s =>
                                        $"• Номер: {s.Number}\nДиагноз: {s.Diagnosis}\n" +
                                        $"Период: {s.StartDate:dd.MM.yyyy} - {s.EndDate:dd.MM.yyyy}\n" +
                                        $"Статус: {s.Status}\nТип: {s.Type}"));

                                await botClient.SendTextMessageAsync(
                                    chatId: chatIdEntry.Key,
                                    text: sickLeaveMessage);
                            }

                            if (!diagnoses.Any() && !referrals.Any() && !prescriptions.Any() && !sickLeaves.Any())
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatIdEntry.Key,
                                    text: "После визита врач не добавил диагнозы, направления или рецепты.");
                            }

                            await connection.ExecuteAsync(
                                "UPDATE Visits SET NotificationSent = 1 WHERE VisitID = @VisitID",
                                new { VisitID = visit.VisitID });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in CheckCompletedVisits: {ex.Message}");
            }
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
                            text: "Введите ваш СНИЛС (в формате XXX-XXX-XXX XX):");
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

                                chatIdToPatientIdMap[chatId] = patient.ID;

                                await ShowSpecialties(chatId);
                                userState.CurrentStep = Step.SelectSpecialty;
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Пациент с таким СНИЛС не найден. Попробуйте еще раз:");
                            }
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат СНИЛС. Введите в формате XXX-XXX-XXX XX:");
                        }
                        break;

                    case Step.SelectSpecialty:
                        var specialties = await GetSpecialties();
                        if (specialties.Contains(messageText))
                        {
                            userState.Specialty = messageText;
                            await ShowDoctors(chatId, messageText);
                            userState.CurrentStep = Step.SelectDoctor;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Пожалуйста, выберите специальность из списка:");
                        }
                        break;

                    case Step.SelectDoctor:
                        var doctors = await GetDoctorsBySpecialty(userState.Specialty);
                        var doctor = doctors.FirstOrDefault(d => d.FIO == messageText);
                        if (doctor != null)
                        {
                            userState.DoctorId = doctor.UserID;
                            await ShowAvailableDates(chatId, doctor.UserID);
                            userState.CurrentStep = Step.SelectDate;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Пожалуйста, выберите врача из списка:");
                        }
                        break;

                    case Step.SelectDate:
                        if (DateTime.TryParse(messageText, out var selectedDate))
                        {
                            userState.SelectedDate = selectedDate;
                            await ShowAvailableTimes(chatId, userState.DoctorId, selectedDate);
                            userState.CurrentStep = Step.SelectTime;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат даты. Пожалуйста, введите дату в формате ДД.ММ.ГГГГ:");
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
                                    text: "Введите примечание к записи (или нажмите /skip чтобы пропустить):");
                                userState.CurrentStep = Step.EnterNotes;
                            }
                            else
                            {
                                await botClient.SendTextMessageAsync(
                                    chatId: chatId,
                                    text: "Это время уже занято. Пожалуйста, выберите другое время:");
                            }
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: chatId,
                                text: "Неверный формат времени. Пожалуйста, выберите время из списка:");
                        }
                        break;

                    case Step.EnterNotes:
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
                                 $"Примечание: {(userState.Notes ?? "не указано")}");

                        userStates.Remove(chatId);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Произошла ошибка. Пожалуйста, начните заново.");
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

        private static async Task<Person> GetPatientBySnils(string snils)
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

        private static async Task ShowSpecialties(long chatId)
        {
            var specialties = await GetSpecialties();
            var keyboard = new ReplyKeyboardMarkup(
                specialties.Select(s => new KeyboardButton(s)).Chunk(2).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите специальность врача:",
                replyMarkup: keyboard);
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

        private static async Task ShowDoctors(long chatId, string specialty)
        {
            var doctors = await GetDoctorsBySpecialty(specialty);
            var keyboard = new ReplyKeyboardMarkup(
                doctors.Select(d => new KeyboardButton(d.FIO)).Chunk(2).ToArray())
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите врача:",
                replyMarkup: keyboard);
        }

        private static async Task ShowAvailableDates(long chatId, int doctorId)
        {
            var availableDates = new List<DateTime>();
            for (int i = 0; i < 14; i++)
            {
                var date = DateTime.Today.AddDays(i + 1);
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
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите дату:",
                replyMarkup: keyboard);
        }

        private static async Task ShowAvailableTimes(long chatId, int doctorId, DateTime date)
        {
            var bookedTimes = await GetBookedTimes(doctorId, date);
            var availableTimes = new List<string>();

            var startTime = new TimeSpan(8, 0, 0);
            var endTime = new TimeSpan(18, 0, 0);
            var lunchStart = new TimeSpan(13, 0, 0);
            var lunchEnd = new TimeSpan(14, 0, 0);

            for (var time = startTime; time < endTime; time = time.Add(new TimeSpan(0, 30, 0)))
            {
                if (time >= lunchStart && time < lunchEnd) continue;

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
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Выберите время:",
                replyMarkup: keyboard);
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

        private static async Task CreateVisit(int patientId, int doctorId, DateTime visitDateTime, string notes)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO Visits (PatientID, EmployeeID, VisitDateTime, Notes, NotificationSent) " +
                    "VALUES (@PatientId, @DoctorId, @VisitDateTime, @Notes, 0)",
                    new
                    {
                        PatientId = patientId,
                        DoctorId = doctorId,
                        VisitDateTime = visitDateTime,
                        Notes = notes
                    });
            }
        }
    }

    public class CompletedVisit
    {
        public int VisitID { get; set; }
        public int PatientID { get; set; }
        public int EmployeeID { get; set; }
        public DateTime VisitDateTime { get; set; }
        public DateTime EndDateTime { get; set; }
        public string Notes { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public string DoctorName { get; set; }
    }

    public class UserState
    {
        public Step CurrentStep { get; set; }
        public int PatientId { get; set; }
        public string Snils { get; set; }
        public string Specialty { get; set; }
        public int DoctorId { get; set; }
        public DateTime SelectedDate { get; set; }
        public DateTime VisitDateTime { get; set; }
        public string Notes { get; set; }
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
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public DateTime BirthDate { get; set; }
        public string PhoneNumber { get; set; }
        public string Gender { get; set; }
        public string SNILS { get; set; }
        public string RegistrationAddress { get; set; }
        public string ActualAddress { get; set; }
    }

    public class Doctor
    {
        public int UserID { get; set; }
        public string FIO { get; set; }
    }

    public class Diagnosis
    {
        public string DiagnosisName { get; set; }
        public string Description { get; set; }
        public DateTime DateCreated { get; set; }
    }

    public class MedicalReferral
    {
        public string ReferralNumber { get; set; }
        public string Purpose { get; set; }
        public string Speciality { get; set; }
        public string ServiceType { get; set; }
        public DateTime ReferralDate { get; set; }
    }

    public class Prescription
    {
        public string MedicationName { get; set; }
        public string Dosage { get; set; }
        public DateTime IssueDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }

    public class SickLeave
    {
        public string Number { get; set; }
        public string Diagnosis { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Status { get; set; }
        public string Type { get; set; }
    }
}