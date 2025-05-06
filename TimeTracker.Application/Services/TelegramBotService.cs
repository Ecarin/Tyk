using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TimeTracker.Application.Interfaces;
using TimeTracker.Domain.Entities;

namespace TimeTracker.Application.Services;

public class TelegramBotService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramBotService> _logger;
    private ITelegramBotClient _botClient;
    private CancellationTokenSource _cts;
    private Timer _minuteTimer;
    private readonly TimeZoneInfo _iranTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
    private readonly Dictionary<long, int> _activeTopics = new();
    private readonly Dictionary<long, long> _pendingConfirmations = new();

    public TelegramBotService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TelegramBotService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _cts = new CancellationTokenSource();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Telegram Bot Service");
        // Fetch bot token from environment variable
        var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken))
        {
            throw new InvalidOperationException("Telegram Bot Token not found in environment variables.");
        }

        _botClient = new TelegramBotClient(botToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>(),
            DropPendingUpdates = true
        };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token
        );

        _minuteTimer = new Timer(OnMinuteTick, null,
            GetTimeUntilNextMinute(),
            TimeSpan.FromMinutes(1));

        // Set up the midnight reset timer
        OnMidnightReset(null);

        var me = await _botClient.GetMe(cancellationToken);
        _logger.LogInformation("Bot started successfully as @{Username}", me.Username);
    }

    private TimeSpan GetTimeUntilNextMinute()
    {
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
        var nextMinute = new DateTime(iranNow.Year, iranNow.Month, iranNow.Day, iranNow.Hour, iranNow.Minute, 0)
            .AddMinutes(1);
        return nextMinute - iranNow;
    }

    private async void OnMinuteTick(object state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();
            var chats = await repository.GetTrackedChatsAsync();

            var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
            var today = iranNow.Date;

            foreach (var chatId in chats)
            {
                var chatEntries = await repository.GetUserEntriesAsync(chatId, today, iranNow);
                var firstMessage = await repository.GetChatMessageAsync(chatId);

                // Reset status if it's a new day and send a new status message
                if (firstMessage == null || firstMessage.LastUpdated.Date != today)
                {
                    // Set all users to "out" if not already "out"
                    foreach (var entry in chatEntries.Where(e => e.IsActive))
                    {
                        var entryOut = new TimeEntry
                        {
                            UserId = entry.UserId,
                            ChatId = chatId,
                            Timestamp = iranNow,
                            Action = "out",
                            IsActive = false
                        };
                        await repository.AddEntryAsync(entryOut);
                    }

                    // Send the new status message for the day
                    await UpdateStatusMessage(chatId, repository);

                    // Update the chat message with the new day's status
                    if (firstMessage != null)
                    {
                        firstMessage.LastUpdated = iranNow;
                        await repository.SaveChatMessageAsync(firstMessage);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Minute update error");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();

        try
        {
            switch (update.Type)
            {
                case UpdateType.CallbackQuery:
                    await HandleCallbackQuery(update.CallbackQuery!, repository);
                    break;
                case UpdateType.Message:
                    await HandleMessage(update.Message!, repository);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update");
        }
    }

    private async Task HandleCallbackQuery(CallbackQuery query, ITimeEntryRepository repository)
    {
        if (query.Data == "confirm_out")
        {
            if (!_pendingConfirmations.TryGetValue(query.Message!.MessageId, out var userId) || userId != query.From.Id)
            {
                await _botClient.AnswerCallbackQuery(query.Id, "❌ این تاییدیه مربوط به شما نیست");
                return;
            }

            await HandleConfirmedOut(query, repository);
        }
        else if (query.Data == "cancel_out")
        {
            if (!_pendingConfirmations.TryGetValue(query.Message!.MessageId, out var userId) || userId != query.From.Id)
            {
                await _botClient.AnswerCallbackQuery(query.Id, "❌ این تاییدیه مربوط به شما نیست");
                return;
            }

            await CancelOutConfirmation(query, repository);
        }
        else
        {
            await HandleButtonClick(query, repository);
        }
    }

    private async Task HandleConfirmedOut(CallbackQuery query, ITimeEntryRepository repository)
    {
        var user = query.From;
        var chat = query.Message!.Chat;
        var iranTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);

        var entry = new TimeEntry
        {
            UserId = user.Id,
            Username = user.Username ?? $"{user.FirstName} {user.LastName}".Trim(),
            ChatId = chat.Id,
            Timestamp = iranTime,
            Action = "out",
            IsActive = false
        };

        // Ensure the user is not already marked "out" today
        var todayEntries = await repository.GetUserEntriesAsync(chat.Id, iranTime.Date, iranTime);
        if (todayEntries.Any(e => e.UserId == user.Id && e.Action == "out"))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "⚠️ شما قبلاً وضعیت خروج را ثبت کرده‌اید.");
            return;
        }

        await repository.AddEntryAsync(entry);
        await UpdateStatusMessage(chat.Id, repository);
        await _botClient.AnswerCallbackQuery(query.Id, "✅ خارج شدن شما ثبت شد");
        _pendingConfirmations.Remove(query.Message.MessageId);
    }

    private async Task CancelOutConfirmation(CallbackQuery query, ITimeEntryRepository repository)
    {
        await UpdateStatusMessage(query.Message!.Chat.Id, repository);
        await _botClient.AnswerCallbackQuery(query.Id, "❌ خارج شدن لغو شد");
        _pendingConfirmations.Remove(query.Message.MessageId);
    }


    private async Task HandleButtonClick(CallbackQuery query, ITimeEntryRepository repository)
    {
        try
        {
            await _botClient.AnswerCallbackQuery(query.Id, "در حال پردازش...");

            var user = query.From;
            var chat = query.Message!.Chat;
            var iranTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);

            var todayEntries = await repository.GetUserEntriesAsync(chat.Id, iranTime.Date, iranTime);
            var userTodayEntries = todayEntries.Where(e => e.UserId == user.Id).ToList();
            var lastAction = userTodayEntries.OrderByDescending(e => e.Timestamp).FirstOrDefault();

            // Check if the user has already performed the same action today
            if (lastAction?.Action == query.Data)
            {
                await _botClient.AnswerCallbackQuery(query.Id,
                    $"⚠️ شما هم اکنون در وضعیت {TranslateAction(query.Data)} هستید.");
                return;
            }

            // Handle the "out" action (confirm out or cancel out)
            if (query.Data == "out")
            {
                await ShowOutConfirmation(query);
                return;
            }

            var entry = new TimeEntry
            {
                UserId = user.Id,
                Username = user.Username ?? $"{user.FirstName} {user.LastName}".Trim(),
                ChatId = chat.Id,
                Timestamp = iranTime,
                Action = query.Data!,
                IsActive = query.Data == "in"
            };

            await repository.AddEntryAsync(entry);
            await UpdateStatusMessage(chat.Id, repository);
            await _botClient.AnswerCallbackQuery(query.Id, $"وضعیت ثبت شد: {TranslateAction(query.Data!)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling button click");
            await _botClient.AnswerCallbackQuery(query.Id, "خطا در پردازش درخواست");
        }
    }

    private async Task ShowOutConfirmation(CallbackQuery query)
    {
        _pendingConfirmations[query.Message!.MessageId] = query.From.Id;

        var confirmKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ تایید خروج", "confirm_out") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ انصراف", "cancel_out") }
        });

        await _botClient.EditMessageText(
            chatId: query.Message.Chat.Id,
            messageId: query.Message.MessageId,
            text: $"⚠️ <b>تایید خروج برای {query.From.FirstName}</b>\n\n" +
                  "آیا مطمئن هستید می‌خواهید خارج شوید؟\n" +
                  "این عمل غیرقابل بازگشت است!",
            parseMode: ParseMode.Html,
            replyMarkup: confirmKeyboard);
    }

    private async Task HandleMessage(Message message, ITimeEntryRepository repository)
    {
        if (message.Text?.StartsWith("/") == true)
        {
            await HandleCommand(message, repository);
        }
        else if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
        {
            await UpdateStatusMessage(message.Chat.Id, repository);
        }
    }

    private async Task HandleCommand(Message message, ITimeEntryRepository repository)
    {
        var commandParts = message.Text!.Split(' ');
        var command = commandParts[0].ToLower();

        switch (command)
        {
            case "/start":
                await SendWelcomeMessage(message.Chat.Id, message.MessageThreadId);
                break;

            case "/report":
                if (await IsAdmin(message.Chat.Id, message.From!.Id))
                {
                    var period = commandParts.Length > 1 ? commandParts[1] : "day";
                    await HandleReportCommand(message.Chat.Id, period, repository, message.MessageThreadId);
                }
                else
                {
                    await SendTempMessage(message.Chat.Id, "❌ فقط ادمین‌ها می‌توانند گزارش دریافت کنند",
                        message.MessageThreadId);
                }

                break;

            case "/set_topic":
                if (await IsAdmin(message.Chat.Id, message.From!.Id) && message.MessageThreadId.HasValue)
                {
                    _activeTopics[message.Chat.Id] = message.MessageThreadId.Value;
                    await SendTempMessage(message.Chat.Id,
                        $"✅ وضعیت‌ها در این تاپیک مدیریت خواهد شد (تاپیک #{message.MessageThreadId})",
                        message.MessageThreadId);
                }

                break;

            default:
                await SendTempMessage(message.Chat.Id, "❌ دستور ناشناخته", message.MessageThreadId);
                break;
        }
    }

    private async Task HandleReportCommand(long chatId, string period, ITimeEntryRepository repository,
        int? threadId = null)
    {
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
        (DateTime fromDate, DateTime toDate) reportPeriod;

        if (period.ToLower() == "year")
        {
            // Get report for the entire year
            reportPeriod = (new DateTime(iranNow.Year, 1, 1), iranNow);
        }
        else
        {
            // Handle months
            reportPeriod = period.ToLower() switch
            {
                "month" => (new DateTime(iranNow.Year, iranNow.Month, 1), iranNow),
                _ => (iranNow.Date, iranNow)
            };
        }

        var entries = await repository.GetUserEntriesAsync(chatId, reportPeriod.fromDate, reportPeriod.toDate);
        var report = GenerateReportText(entries, reportPeriod.fromDate, reportPeriod.toDate);

        var filePath = await GenerateReportExcel(entries, reportPeriod.fromDate, reportPeriod.toDate);

        // Open the file as a stream
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            var inputFile = new InputFileStream(fileStream, "your_file.xlsx");

            // Send the document
            await _botClient.SendDocument(
                chatId: chatId,
                document: inputFile,
                caption: "گزارش زمان کاری",
                messageThreadId: threadId);
        }
    }

    public async Task<string> GenerateReportExcel(List<TimeEntry> entries, DateTime from, DateTime to)
    {
        // Convert 'from' and 'to' dates to Persian calendar
        var persianCalendar = new PersianCalendar();
        var persianFrom = new DateTime(persianCalendar.GetYear(from), persianCalendar.GetMonth(from),
            persianCalendar.GetDayOfMonth(from));
        var persianTo = new DateTime(persianCalendar.GetYear(to), persianCalendar.GetMonth(to),
            persianCalendar.GetDayOfMonth(to));

        // Create a new workbook and worksheet
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Report");

        // Set up headers
        worksheet.Cell(1, 1).Value = "نام کاربر";
        worksheet.Cell(1, 2).Value = "تعداد روزهای کاری";
        worksheet.Cell(1, 3).Value = "مجموع ساعت‌های کاری";

        // Group entries by user
        var userGroups = entries
            .Where(e => e.Timestamp.Date >= persianFrom && e.Timestamp.Date <= persianTo && e.Action == "in")
            .GroupBy(e => e.UserId);

        int row = 2;
        foreach (var group in userGroups)
        {
            var user = group.First(); // Assuming the first entry contains user info
            var totalWorkTime = CalculateTotalWorkTime(group.ToList());

            worksheet.Cell(row, 1).Value = user.Username ?? $"{user.FirstName} {user.LastName}".Trim();
            worksheet.Cell(row, 2).Value = group.Select(e => e.Timestamp.Date).Distinct().Count();
            worksheet.Cell(row, 3).Value = totalWorkTime.ToString(@"hh\:mm");

            row++;
        }

        // Format the columns
        worksheet.Columns().AdjustToContents();

        // Generate the file name with Persian date
        var fileName = $"Salmej_Export_{persianFrom:yyyyMMdd}.xlsx";
        var filePath = Path.Combine("Reports", fileName);

        // Ensure the directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));

        // Save the workbook to a file
        workbook.SaveAs(filePath);

        return filePath;
    }

    private TimeSpan CalculateTotalWorkTime(List<TimeEntry> entries)
    {
        TimeSpan total = TimeSpan.Zero;
        DateTime? lastInTime = null;

        foreach (var entry in entries.OrderBy(e => e.Timestamp))
        {
            if (entry.Action == "in")
            {
                lastInTime = entry.Timestamp;
            }
            else if (lastInTime.HasValue && (entry.Action == "break" || entry.Action == "out"))
            {
                total += entry.Timestamp - lastInTime.Value;
                if (entry.Action == "out")
                {
                    lastInTime = null;
                }
            }
        }

        return total;
    }

    private string GenerateReportText(List<TimeEntry> entries, DateTime from, DateTime to)
    {
        var sb = new StringBuilder("<b>📊 گزارش زمانی کار</b>\n");
        sb.AppendLine($"<i>از {ConvertToPersianDate(from)} تا {ConvertToPersianDate(to)}</i>");
        sb.AppendLine("<code>------------------------------------</code>");

        var userGroups = entries
            .GroupBy(e => new { e.UserId, e.Username })
            .OrderBy(g => g.Key.Username);

        foreach (var group in userGroups)
        {
            var totalWorkTime = CalculateTotalWorkTime(group.ToList());
            sb.AppendLine($"👤 {group.Key.Username}: {totalWorkTime:hh\\:mm}");
        }

        return sb.ToString();
    }

    private async Task SendTempMessage(long chatId, string text, int? threadId = null)
    {
        await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            messageThreadId: threadId);
    }

    private async Task UpdateStatusMessage(long chatId, ITimeEntryRepository repository)
    {
        try
        {
            var threadId = _activeTopics.TryGetValue(chatId, out var topicId) ? topicId : (int?)null;
            var chatMessage = await repository.GetChatMessageAsync(chatId);
            var statusText = await GenerateStatusText(chatId, repository);
            var keyboard = CreateStatusKeyboard();

            if (chatMessage != null)
            {
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: chatMessage.MessageId,
                    text: statusText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard);

                chatMessage.LastUpdated = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
                await repository.SaveChatMessageAsync(chatMessage);
            }
            else
            {
                var newMessage = await _botClient.SendMessage(
                    chatId: chatId,
                    text: statusText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    messageThreadId: threadId);

                await repository.SaveChatMessageAsync(new ChatMessage
                {
                    ChatId = chatId,
                    MessageId = newMessage.MessageId,
                    LastUpdated = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating status message");
        }
    }

    private async Task<string> GenerateStatusText(long chatId, ITimeEntryRepository repository)
    {
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTimeZone);
        var todayEntries = await repository.GetUserEntriesAsync(chatId, iranNow.Date, iranNow);

        var userStatuses = todayEntries
            .GroupBy(e => e.UserId)
            .Select(g => new
            {
                User = g.First(),
                Latest = g.OrderByDescending(e => e.Timestamp).First(),
                AllEntries = g.OrderBy(e => e.Timestamp).ToList(),
                WorkTime = CalculateTotalWorkTime(g.ToList())
            })
            .OrderBy(u => u.User.Username);

        var sb = new StringBuilder();
        sb.AppendLine("<b>🕒 وضعیت تیم</b>");
        sb.AppendLine($"<i>{ConvertToPersianDateTime(iranNow)}</i>");
        sb.AppendLine("<code>────────────────────────────</code>");

        foreach (var user in userStatuses)
        {
            var firstIn = user.AllEntries.FirstOrDefault(e => e.Action == "in");
            var lastOut = user.AllEntries.LastOrDefault(e => e.Action == "out");
            var lastActivity = user.AllEntries.LastOrDefault();

            sb.AppendLine($"<b>{GetActionEmoji(user.Latest.Action)} {user.User.Username}</b>");
            sb.AppendLine($"⏳ کارکرد امروز: <code>{user.WorkTime:hh\\:mm}</code>");

            if (firstIn != null)
                sb.AppendLine($"🟢 شروع کار: <code>{firstIn.Timestamp:HH:mm}</code>");

            if (lastActivity != null)
            {
                var status = user.Latest.Action == "out"
                    ? "🔴 خارج شده"
                    : $"🔄 آخرین فعالیت: <code>{lastActivity.Timestamp:HH:mm}</code> ({TranslateAction(lastActivity.Action)})";
                sb.AppendLine(status);
            }

            if (lastOut != null)
            {
                sb.AppendLine($"🔴 پایان کار: <code>{lastOut.Timestamp:HH:mm}</code>");
                sb.AppendLine("🚫 امکان حضور مجدد امروز وجود ندارد");
            }

            sb.AppendLine("<code>────────────────────────────</code>");
        }

        // sb.AppendLine("<i>🟢 حضور | 🟡 استراحت | 🔴 خروج (غیرقابل بازگشت)</i>");

        return sb.ToString();
    }

    private string GetActionEmoji(string action) => action switch
    {
        "in" => "🟢",
        "break" => "🟡",
        "out" => "🔴",
        _ => "⚪"
    };

    private string TranslateAction(string action) => action switch
    {
        "in" => "حضور",
        "break" => "استراحت",
        "out" => "خروج",
        _ => action
    };

    private string ConvertToPersianDate(DateTime date)
    {
        var persianCalendar = new PersianCalendar();
        return
            $"{persianCalendar.GetYear(date)}/{persianCalendar.GetMonth(date):00}/{persianCalendar.GetDayOfMonth(date):00}";
    }

    private string ConvertToPersianDateTime(DateTime date)
    {
        return $"{ConvertToPersianDate(date)} {date:HH:mm}";
    }

    private InlineKeyboardMarkup CreateStatusKeyboard() => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🟢In", "in"),
            InlineKeyboardButton.WithCallbackData("🟡Break", "break"),
            InlineKeyboardButton.WithCallbackData("🔴Out", "out")
        }
    });

    private async Task SendWelcomeMessage(long chatId, int? threadId = null)
    {
        await _botClient.SendMessage(
            chatId: chatId,
            text: "🕒 <b>ربات ثبت زمان کار</b>\n\n" +
                  "از دکمه‌های زیر برای ثبت وضعیت کاری خود استفاده کنید!\n" +
                  "ادمین‌ها می‌توانند از /report برای دریافت گزارش استفاده کنند",
            parseMode: ParseMode.Html,
            replyMarkup: CreateStatusKeyboard(),
            messageThreadId: threadId);
    }

    private async Task<bool> IsAdmin(long chatId, long userId)
    {
        try
        {
            var chatMember = await _botClient.GetChatMember(
                chatId: chatId,
                userId: userId);
            return chatMember.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch
        {
            return false;
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram API error");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _minuteTimer?.Dispose();
        _cts.Cancel();
        _logger.LogInformation("Bot service stopped");
    }

    public void Dispose()
    {
        _minuteTimer?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void OnMidnightReset(object state)
    {
        try
        {
            var iranMidnight =
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.Date.AddDays(1),
                    _iranTimeZone); // Midnight in Iran timezone
            var timeUntilMidnight = iranMidnight - DateTime.UtcNow;

            if (timeUntilMidnight <= TimeSpan.Zero) return;

            // Timer that will fire exactly at midnight
            _minuteTimer = new Timer(OnMinuteTick, null, timeUntilMidnight, TimeSpan.FromDays(1));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during midnight reset timer setup");
        }
    }
}