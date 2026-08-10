using System.Runtime.CompilerServices;

// Exposes the row-parsing seams on GoogleSheetsControlLogBase so sheet parsing can be unit tested
// without calling the Google Sheets API.
[assembly: InternalsVisibleTo("RedMist.TimingAndScoringService.Tests")]
