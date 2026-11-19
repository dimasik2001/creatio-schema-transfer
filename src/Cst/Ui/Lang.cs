using Spectre.Console;

namespace Cst.Ui;

/// <summary>UI languages. The English text is the key; a missing translation shows the English text.</summary>
public enum UiLanguage { English, Ukrainian }

/// <summary>
/// Texts of the console UI: <c>T(english)</c> for fixed text, <c>F(englishFormat, x)</c> for text with values
/// (string.Format placeholders). Only the UI is translated; cst's own messages (log lines, checks, Creatio errors,
/// the dry-run plan, saved lists) stay in English, like the CLI. tests\ui-lang-check.ps1 lists keys without a
/// Ukrainian text.
/// </summary>
public static partial class L
{
    public static UiLanguage Language { get; set; } = UiLanguage.English;

    public static string Code => Language == UiLanguage.Ukrainian ? "uk" : "en";

    public static UiLanguage Parse(string? code) =>
        string.Equals(code, "uk", StringComparison.OrdinalIgnoreCase) ? UiLanguage.Ukrainian : UiLanguage.English;

    public static string T(string english) =>
        Language == UiLanguage.Ukrainian && Uk.TryGetValue(english, out var uk) ? uk : english;

    public static string F(string english, params object?[] args) => string.Format(T(english), args);

    /// <summary>A selection prompt with the "more choices" hint in the UI language.</summary>
    public static SelectionPrompt<TItem> Select<TItem>() where TItem : notnull =>
        new SelectionPrompt<TItem>().MoreChoicesText(T("[grey](Move up and down to reveal more choices)[/]"));

    /// <summary>A multi-selection prompt with its hints in the UI language.</summary>
    public static MultiSelectionPrompt<TItem> Multi<TItem>() where TItem : notnull =>
        new MultiSelectionPrompt<TItem>()
            .MoreChoicesText(T("[grey](Move up and down to reveal more choices)[/]"))
            .InstructionsText(T("[grey](Press [blue]<space>[/] to toggle a choice, [green]<enter>[/] to accept)[/]"));

    /// <summary>A text prompt with the validation messages in the UI language.</summary>
    public static TextPrompt<string> Text(string question) =>
        new TextPrompt<string>(question)
        {
            ValidationErrorMessage = T("[red]Invalid input[/]"),
        };
}
