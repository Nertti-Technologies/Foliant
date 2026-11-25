namespace Foliant.Mcp;

/// <summary>
/// Server configuration, bound from the "Foliant" section. Env-var overrides use the standard
/// double-underscore convention (e.g. <c>Foliant__ModelsDir</c>), which is what MCP client
/// <c>env</c> blocks supply. ADR-0005 D8.
/// </summary>
public sealed class FoliantMcpOptions
{
    public const string SectionName = "Foliant";

    /// <summary>Directory of pre-downloaded ONNX models (the offline deployment pattern). Empty →
    /// Foliant's SHA-256-verified ModelCache downloads on first use.</summary>
    public string? ModelsDir { get; set; }

    /// <summary>Optional customer template store (SQLite). Empty → bundled federal templates only.</summary>
    public string? TemplatesDbPath { get; set; }

    /// <summary>Hard cap on pages processed per extraction run. Enforced in code, not by the prompt.</summary>
    public int MaxPages { get; set; } = 2000;

    /// <summary>Route pages through the ZeroDep structural fast lane (production parity with
    /// FoliantView). With the flag off the orchestrator delegates verbatim to the pipeline.</summary>
    public bool UseZeroDepFastLane { get; set; } = true;

    /// <summary>Documents at or under this page count may use the synchronous extract_summary tool;
    /// larger documents must go through the start_extraction run-ticket. ADR-0005 open item 4.</summary>
    public int SummarySyncPageLimit { get; set; } = 10;

    /// <summary>Largest PDF a tool may read, in bytes; checked before reading (ADR-0010 T4). Default
    /// 512 MiB, an estimate shared with Foliant.DocumentExtraction's limit: about 16 times the largest
    /// of 10 real-world books sampled in testing (31 MB). Memory: one copy of the file per tool call in
    /// progress or queued (extraction runs wait in an unbounded queue holding their bytes). Values above
    /// Array.MaxLength are treated as Array.MaxLength.</summary>
    public long MaxInputBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Folders tools may read PDFs from, separated by the platform path separator (':' on
    /// macOS/Linux, ';' on Windows), e.g. <c>Foliant__AllowedRoots</c>. Symbolic links are resolved
    /// before the check. Empty → any path the server's user can read (a startup warning says so).</summary>
    public string? AllowedRoots { get; set; }
}

/// <summary>
/// Data-governance switch, bound from the "Privacy" section (<c>Privacy__BlockSensitivePages</c>).
/// Whatever a tool returns is read by whichever model the MCP client runs — for hosted clients that
/// is the provider's cloud. ADR-0005 D9.
/// </summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "Privacy";

    /// <summary>When true, the content of pages carrying a detected sensitivity marking (CUI /
    /// classification banners) is withheld from tool returns and replaced with a per-page notice.
    /// Unmarked pages flow normally. Default false — the operator's call.</summary>
    public bool BlockSensitivePages { get; set; }
}
