using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

// RosterRow is defined in RosterExportService.cs

/// <summary>
/// Writes gamertag data to a Google Sheet.
/// Expects a service account credentials JSON file.
/// </summary>
public class GoogleSheetsService
{
    private readonly BotConfig _config;
    private readonly ILogger<GoogleSheetsService> _logger;

    public GoogleSheetsService(IOptions<BotConfig> config, ILogger<GoogleSheetsService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Appends (or updates) a row in the configured Google Sheet.
    /// Columns: Discord Name | EA | Steam | PSN | Xbox | Embark | Bungie
    /// If the Discord user already has a row, it will be updated in place.
    /// </summary>
    public async Task WriteGamertagsAsync(
        string discordName, string ea, string steam, string psn,
        string xbox, string embark, string bungie)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = _config.GoogleSpreadsheetId;
        var sheetName = _config.GoogleSheetName;
        var range = $"{sheetName}!A:G";

        // Try to find an existing row for this Discord user
        var getRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
        var getResponse = await getRequest.ExecuteAsync();

        var newRow = new List<object> { discordName, ea, steam, psn, xbox, embark, bungie };

        if (getResponse.Values is not null)
        {
            for (int i = 0; i < getResponse.Values.Count; i++)
            {
                var row = getResponse.Values[i];
                if (row.Count > 0 && string.Equals(row[0]?.ToString(), discordName, StringComparison.OrdinalIgnoreCase))
                {
                    // Update existing row (sheets are 1-indexed, +1 for header awareness)
                    var updateRange = $"{sheetName}!A{i + 1}:G{i + 1}";
                    var updateBody = new ValueRange { Values = new List<IList<object>> { newRow } };
                    var updateRequest = service.Spreadsheets.Values.Update(updateBody, spreadsheetId, updateRange);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest
                        .ValueInputOptionEnum.USERENTERED;
                    await updateRequest.ExecuteAsync();

                    _logger.LogInformation("Updated gamertags for {DiscordName} in row {Row}", discordName, i + 1);
                    await SortSheetByFirstColumnAsync(service, spreadsheetId, sheetName);
                    return;
                }
            }
        }

        // Append new row
        var appendBody = new ValueRange { Values = new List<IList<object>> { newRow } };
        var appendRequest = service.Spreadsheets.Values.Append(appendBody, spreadsheetId, range);
        appendRequest.ValueInputOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        appendRequest.InsertDataOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await appendRequest.ExecuteAsync();

        _logger.LogInformation("Appended gamertags for {DiscordName}", discordName);

        await SortSheetByFirstColumnAsync(service, spreadsheetId, sheetName);
    }

    /// <summary>
    /// Sorts all data rows (excluding the header) alphabetically by the first column.
    /// </summary>
    private async Task SortSheetByFirstColumnAsync(SheetsService service, string spreadsheetId, string sheetName)
    {
        // Get the sheet ID by name
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
        if (sheet is null)
        {
            _logger.LogWarning("Sheet {SheetName} not found — skipping sort", sheetName);
            return;
        }

        var sheetId = sheet.Properties.SheetId ?? 0;
        var rowCount = sheet.Properties.GridProperties.RowCount ?? 1000;
        var colCount = sheet.Properties.GridProperties.ColumnCount ?? 7;

        var sortRequest = new Request
        {
            SortRange = new SortRangeRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = 1, // skip header row
                    EndRowIndex = rowCount,
                    StartColumnIndex = 0,
                    EndColumnIndex = colCount
                },
                SortSpecs = new List<SortSpec>
                {
                    new SortSpec
                    {
                        DimensionIndex = 0, // column A
                        SortOrder = "ASCENDING"
                    }
                }
            }
        };

        var batchUpdate = new BatchUpdateSpreadsheetRequest
        {
            Requests = new List<Request> { sortRequest }
        };

        var response = await service.Spreadsheets.BatchUpdate(batchUpdate, spreadsheetId).ExecuteAsync();
        _logger.LogInformation("Sorted sheet {SheetName} alphabetically by Discord Name (replies: {Count})",
            sheetName, response.Replies?.Count ?? 0);
    }

    /// <summary>
    /// Ensures the header row exists in the sheet. Call once on startup.
    /// </summary>
    public async Task EnsureHeaderRowAsync()
    {
        try
        {
            var credential = GoogleCredential
                .FromFile(_config.GoogleCredentialsPath)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ClanGuardBot"
            });

            var range = $"{_config.GoogleSheetName}!A1:G1";
            var getRequest = service.Spreadsheets.Values.Get(_config.GoogleSpreadsheetId, range);
            var response = await getRequest.ExecuteAsync();

            if (response.Values is null || response.Values.Count == 0)
            {
                var header = new List<object> { "Discord Name", "EA", "Steam", "PSN", "Xbox", "Embark", "Bungie" };
                var body = new ValueRange { Values = new List<IList<object>> { header } };
                var updateRequest = service.Spreadsheets.Values.Update(body, _config.GoogleSpreadsheetId, range);
                updateRequest.ValueInputOption =
                    SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                _logger.LogInformation("Created header row in Gamertags sheet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure header row in Google Sheet (sheet may not be configured yet)");
        }
    }

    /// <summary>
    /// Writes the full roster to the Roster sheet, replacing all existing data.
    /// </summary>
    public async Task WriteRosterAsync(string guildName, List<RosterRow> rows)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = string.IsNullOrWhiteSpace(_config.RosterSpreadsheetId)
            ? _config.GoogleSpreadsheetId
            : _config.RosterSpreadsheetId;
        var sheetName = _config.RosterSheetName;

        // Ensure the Roster tab exists
        await EnsureSheetTabExistsAsync(service, spreadsheetId, sheetName);

        // Build the data: header + rows
        var allRows = new List<IList<object>>();

        // Header
        allRows.Add(new List<object>
        {
            "Discord Name", "Username", "Rank", "Roles", "Join Date",
            "Messages", "Voice Hours", "Window", "AWOL",
            "Time in Rank", "Last Events VC", "Events At Rank", "Promotable",
            $"Last Updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
        });

        // Data rows
        foreach (var row in rows)
        {
            var timeInRank = row.RankSince.HasValue
                ? FormatDuration(DateTime.UtcNow - row.RankSince.Value)
                : "—";

            var lastEvents = row.LastEventsVc.HasValue
                ? row.LastEventsVc.Value.ToString("yyyy-MM-dd")
                : "Never";

            allRows.Add(new List<object>
            {
                row.DiscordName,
                row.Username,
                row.Rank,
                row.Roles,
                row.JoinDate?.ToString("yyyy-MM-dd") ?? "Unknown",
                row.Messages,
                row.VoiceHours.ToString("F1"),
                $"{row.WindowDays}d",
                row.IsAwol ? "YES" : "",
                timeInRank,
                lastEvents,
                row.EventsAtRank,
                row.IsPromotable ? "YES" : ""
            });
        }

        // Clear existing data and write fresh
        var fullRange = $"{sheetName}!A1:N{allRows.Count + 10}";
        var clearRequest = service.Spreadsheets.Values.Clear(
            new ClearValuesRequest(), spreadsheetId, fullRange);
        await clearRequest.ExecuteAsync();

        var writeRange = $"{sheetName}!A1:N{allRows.Count}";
        var body = new ValueRange { Values = allRows };
        var updateRequest = service.Spreadsheets.Values.Update(body, spreadsheetId, writeRange);
        updateRequest.ValueInputOption =
            SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await updateRequest.ExecuteAsync();

        // Apply alternate row shading
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var sheetId = spreadsheet.Sheets
            .First(s => s.Properties.Title == sheetName)
            .Properties.SheetId;

        var formatRequests = new List<Request>();

        // Light gray for even data rows (0-indexed: row 0 = header, row 1 = first data row)
        var shadedColor = new Color { Red = 0.95f, Green = 0.95f, Blue = 0.95f, Alpha = 1f };
        var whiteColor = new Color { Red = 1f, Green = 1f, Blue = 1f, Alpha = 1f };

        for (int i = 1; i <= rows.Count; i++)
        {
            var bgColor = (i % 2 == 0) ? shadedColor : whiteColor;

            formatRequests.Add(new Request
            {
                AddConditionalFormatRule = new AddConditionalFormatRuleRequest
                {
                    Rule = new ConditionalFormatRule
                    {
                        Ranges = new List<GridRange>
                        {
                            new GridRange
                            {
                                SheetId = sheetId,
                                StartRowIndex = 1,
                                EndRowIndex = rows.Count + 1,
                                StartColumnIndex = 0,
                                EndColumnIndex = 13
                            }
                        },
                        BooleanRule = new BooleanRule
                        {
                            Condition = new BooleanCondition
                            {
                                Type = "CUSTOM_FORMULA",
                                Values = new List<ConditionValue>
                                {
                                    new ConditionValue { UserEnteredValue = "=MOD(ROW(),2)=0" }
                                }
                            },
                            Format = new CellFormat
                            {
                                BackgroundColor = new Color { Red = 0.95f, Green = 0.95f, Blue = 0.95f }
                            }
                        }
                    },
                    Index = 0
                }
            });
        }

        // Also style the header row
        formatRequests.Add(new Request
        {
            RepeatCell = new RepeatCellRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = 0,
                    EndRowIndex = 1,
                    StartColumnIndex = 0,
                    EndColumnIndex = 13
                },
                Cell = new CellData
                {
                    UserEnteredFormat = new CellFormat
                    {
                        BackgroundColor = new Color { Red = 0.2f, Green = 0.2f, Blue = 0.2f, Alpha = 1f },
                        TextFormat = new TextFormat
                        {
                            Bold = true,
                            ForegroundColor = new Color { Red = 1f, Green = 1f, Blue = 1f, Alpha = 1f }
                        }
                    }
                },
                Fields = "userEnteredFormat(backgroundColor,textFormat)"
            }
        });

        var batchFormatRequest = new BatchUpdateSpreadsheetRequest
        {
            Requests = formatRequests
        };
        await service.Spreadsheets.BatchUpdate(batchFormatRequest, spreadsheetId).ExecuteAsync();

        _logger.LogInformation("Wrote {Count} roster rows to sheet {SheetName}", rows.Count, sheetName);
    }

    /// <summary>
    /// Creates a sheet tab if it doesn't already exist in the spreadsheet.
    /// </summary>
    private async Task EnsureSheetTabExistsAsync(SheetsService service, string spreadsheetId, string sheetName)
    {
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        if (spreadsheet.Sheets.Any(s => s.Properties.Title == sheetName))
            return;

        var addSheetRequest = new Request
        {
            AddSheet = new AddSheetRequest
            {
                Properties = new SheetProperties { Title = sheetName }
            }
        };

        var batchUpdate = new BatchUpdateSpreadsheetRequest
        {
            Requests = new List<Request> { addSheetRequest }
        };

        await service.Spreadsheets.BatchUpdate(batchUpdate, spreadsheetId).ExecuteAsync();
        _logger.LogInformation("Created sheet tab {SheetName}", sheetName);
    }

    /// <summary>Formats a TimeSpan as a human-readable duration like "45d" or "3mo 12d".</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        var totalDays = (int)duration.TotalDays;
        if (totalDays < 1) return "< 1d";
        if (totalDays < 30) return $"{totalDays}d";

        var months = totalDays / 30;
        var remainingDays = totalDays % 30;
        if (months < 12)
            return remainingDays > 0 ? $"{months}mo {remainingDays}d" : $"{months}mo";

        var years = months / 12;
        var remainingMonths = months % 12;
        return remainingMonths > 0 ? $"{years}y {remainingMonths}mo" : $"{years}y";
    }
}