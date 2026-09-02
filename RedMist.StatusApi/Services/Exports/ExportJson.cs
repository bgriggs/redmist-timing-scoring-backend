using System.Text.Encodings.Web;
using System.Text.Json;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Shared writer settings for the JSON export files.
/// </summary>
public static class ExportJson
{
    /// <summary>
    /// Options for the downloaded JSON files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Indented, because these files are opened and read by people. An export is something somebody
    /// saves and looks through - often in whatever text editor is to hand - not a payload another
    /// program parses on a hot path, and a session of laps on one line is unreadable in all of them.
    /// </para>
    /// <para>
    /// The relaxed encoder is part of the same goal, not a separate liberty. The default
    /// <see cref="JavaScriptEncoder"/> is built for embedding JSON in HTML, so it escapes anything
    /// that could matter there: <c>O'Brien</c> becomes <c>O\u0027Brien</c>, <c>A &amp; B</c> becomes
    /// <c>A \u0026 B</c>, and every accented letter in a name like <c>José Müller</c> turns into a
    /// <c>\uXXXX</c> pair. Driver names are the field a human actually reads in a lap export and
    /// those surnames are routine, so leaving the default on would have made the pretty-printed file
    /// harder to read than the compact one it replaced.
    /// </para>
    /// <para>
    /// <see cref="JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRange[])"/> over the full
    /// Unicode range is not enough - it still escapes the apostrophe and the ampersand. The relaxed
    /// encoder is safe here because of where these bytes go: an attachment download served as
    /// <c>application/json</c>, never interpolated into a page, so there is no HTML context for the
    /// escaping to protect.
    /// </para>
    /// <para>
    /// This costs roughly half again to twice the bytes, which matters only in that the export size
    /// ceiling is now reached at fewer rows. That is the ceiling doing its job - it bounds the file,
    /// which is the thing the disk cares about, not the row count - and a file that stops early says
    /// so in its own envelope.
    /// </para>
    /// <para>
    /// This is for the downloaded files only. The API's own JSON responses are left to the standard
    /// ASP.NET formatter settings, where the HTML-safe default is the right choice.
    /// </para>
    /// </remarks>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
