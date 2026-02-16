using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

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
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
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
        appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
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
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                _logger.LogInformation("Created header row in Gamertags sheet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure header row in Google Sheet (sheet may not be configured yet)");
        }
    }
}