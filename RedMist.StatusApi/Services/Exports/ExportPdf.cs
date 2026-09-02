using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Shared QuestPDF setup and layout for the export reports.
/// </summary>
/// <remarks>
/// Both reports are the same shape - a title block, one wide table, page numbers - so the page
/// furniture lives here and each report only supplies its columns and rows.
/// </remarks>
public static class ExportPdf
{
    /// <summary>
    /// Applies the process-wide QuestPDF settings, once, before the first report is generated.
    /// </summary>
    /// <remarks>
    /// These are static properties on the library, so they are set in a static constructor: the CLR
    /// runs it exactly once and blocks every other thread until it has, which a hand-rolled
    /// double-checked flag would have to get right by hand. It lives here rather than in startup so
    /// that a test exercising a writer directly gets the same configuration the service runs with -
    /// without it QuestPDF refuses to generate at all, because no license has been declared.
    /// </remarks>
    static ExportPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // The container image carries no fonts. Restricting QuestPDF to the typeface it embeds means
        // the report renders identically in the pod and on a developer machine, instead of silently
        // picking up whatever happens to be installed - and it is why the runtime image needs no
        // fontconfig package.
        QuestPDF.Settings.UseEnvironmentFonts = false;

        // Driver and class names are free text from a race entry form and can contain characters the
        // embedded font has no glyph for. Losing a character is a blemish; refusing to generate the
        // report over it is an outage for the whole export.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    /// <summary>
    /// Forces the static configuration above to run. Calling any member of this class would do it;
    /// this exists so a caller that only uses the writers can be explicit about the dependency.
    /// </summary>
    public static void EnsureConfigured()
    {
    }

    /// <summary>
    /// Builds a landscape report around a single table.
    /// </summary>
    /// <param name="context">What the export covers; drives the title block.</param>
    /// <param name="subtitle">One line describing the contents, e.g. the car and lap count.</param>
    /// <param name="notes">
    /// Ways this report is not the whole truth - truncation, rows that could not be read. Printed on
    /// every page's header in red, because a reader who cannot see them has no way to tell an export
    /// that lost rows from one that did not.
    /// </param>
    /// <param name="composeTable">Supplies the table's columns, header and rows.</param>
    /// <returns>The document, ready to generate.</returns>
    public static IDocument Build(ExportContext context, string subtitle, IReadOnlyList<string> notes,
        Action<TableDescriptor> composeTable)
    {
        EnsureConfigured();

        var eventName = string.IsNullOrWhiteSpace(context.EventName)
            ? $"Event {context.EventId}"
            : context.EventName;
        var sessionName = string.IsNullOrWhiteSpace(context.SessionName)
            ? $"Session {context.SessionId}"
            : context.SessionName;

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(18);
                page.DefaultTextStyle(style => style.FontSize(7));

                page.Header().Column(column =>
                {
                    column.Item().Text(eventName).FontSize(13).SemiBold();
                    column.Item().Text($"{sessionName} - {subtitle}").FontSize(9);
                    column.Item().Text($"Generated {context.GeneratedUtc:yyyy-MM-dd HH:mm:ss} UTC")
                        .FontSize(7).FontColor(Colors.Grey.Darken1);
                    foreach (var note in notes)
                    {
                        column.Item().PaddingTop(2).Text(note)
                            .FontSize(7).FontColor(Colors.Red.Darken1);
                    }
                    column.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(6).Table(composeTable);

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7).FontColor(Colors.Grey.Darken1));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
    }

    /// <summary>Emits the table's header row.</summary>
    /// <param name="table">The table being composed.</param>
    /// <param name="labels">Column labels, one per declared column.</param>
    public static void HeaderRow(TableDescriptor table, params string[] labels)
    {
        table.Header(header =>
        {
            foreach (var label in labels)
                header.Cell().Element(HeaderCell).Text(label).SemiBold();
        });
    }

    /// <summary>Emits one body row.</summary>
    /// <param name="table">The table being composed.</param>
    /// <param name="values">Cell values, one per declared column.</param>
    public static void BodyRow(TableDescriptor table, params string[] values)
    {
        foreach (var value in values)
            table.Cell().Element(BodyCell).Text(value);
    }

    /// <summary>
    /// Renders a position for display, blank when there is not one. Shares
    /// <see cref="CsvFormat.Position"/> so the PDF and the CSV of the same session never disagree
    /// about whether a car had a position on a lap.
    /// </summary>
    /// <param name="position">The position value from the lap row.</param>
    /// <returns>The position, or an empty string when there is not one.</returns>
    public static string Position(int position) => CsvFormat.Position(position);

    private static IContainer HeaderCell(IContainer container) => container
        .Background(Colors.Grey.Lighten3)
        .BorderBottom(1)
        .BorderColor(Colors.Grey.Darken1)
        .PaddingVertical(3)
        .PaddingHorizontal(2);

    private static IContainer BodyCell(IContainer container) => container
        .BorderBottom(0.5f)
        .BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(2)
        .PaddingHorizontal(2);
}
