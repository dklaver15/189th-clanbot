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