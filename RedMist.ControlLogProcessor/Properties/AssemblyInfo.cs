using System.Runtime.CompilerServices;

// Exposes the announcement parsing/sync seams on the background services so they can be unit tested
// without running their polling loops.
[assembly: InternalsVisibleTo("RedMist.TimingAndScoringService.Tests")]
