namespace Scraping

open System
open System.IO
open Microsoft.Extensions.Configuration
open Serilog

/// Configuration + logging, mirroring the twitter_bot layout:
/// appsettings.json next to the binary, Serilog with a compact JSON file sink.
module Settings =

    let private basePath = AppContext.BaseDirectory

    let configuration =
        ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional = true, reloadOnChange = false)
            .Build()
        :> IConfiguration

    let connectionString =
        configuration.GetValue<string>("database:connection_string", "")

    let ytDlpPath =
        configuration.GetValue<string>("yt_dlp:path", "utils/yt-dlp.exe")

    /// YouTube increasingly answers "Sign in to confirm you're not a bot".
    /// Pointing yt-dlp at a logged-in browser profile fixes it:
    /// `"chrome"`, `"firefox"`, `"edge"`, or `"chrome:Profile 1"`.
    let cookiesFromBrowser =
        configuration.GetValue<string>("yt_dlp:cookies_from_browser", "")

    /// Escape hatch for any other yt-dlp flag, e.g. `--extractor-args ...`.
    let extraArgs =
        configuration.GetValue<string>("yt_dlp:extra_args", "")

    let workDir =
        configuration.GetValue<string>("work_dir", "work")

    // ---- VPN rotation (AmneziaWG) -------------------------------------------
    // Installing a tunnel needs administrator rights; when the scraper is not
    // elevated the calls fail and the crawl simply continues on the current IP.
    let vpnExe =
        configuration.GetValue<string>("vpn:exe", @"C:\Program Files\AmneziaWG\AmneziaWG.exe")

    let vpnConfigsDir =
        configuration.GetValue<string>("vpn:configs_dir", "utils/vpn")

    let vpnEnabled =
        configuration.GetValue<bool>("vpn:enabled", false)

    /// Rotate after this many expanded videos (0 = only on rate limits).
    let vpnRotateEvery =
        configuration.GetValue<int>("vpn:rotate_every_videos", 0)

    /// Time to let the tunnel come up before the next request.
    let vpnSettleMs =
        configuration.GetValue<int>("vpn:settle_ms", 6000)

    /// Backoff base for transient failures: `attempts² × base` seconds, so the
    /// retries land at 2, 8, 18 and 32 minutes. The old base of 30 s put the
    /// whole 5-attempt budget inside 15 minutes — shorter than the block it was
    /// waiting for, which turned a perfectly good video into `failed`.
    let retryBaseSeconds =
        configuration.GetValue<int>("scan:retry_base_seconds", 120)

    /// Name of this scraping node, stored on every video it discovers. Several
    /// machines can scrape into separate databases and be merged later; this is
    /// what keeps "which machine found this" answerable after the merge.
    let node =
        configuration.GetValue<string>("node", Environment.MachineName)

    let logPath = Path.Combine(workDir, "scraping.log")

    let initLogging () =
        Directory.CreateDirectory workDir |> ignore

        // No console sink on purpose: the scan draws a live progress line with
        // '\r', and interleaved log lines would shred it. Console output is
        // handled by Scan.render, everything else goes to the JSON log file.
        Log.Logger <-
            LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Serilog.Formatting.Compact.CompactJsonFormatter(),
                    logPath,
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = Nullable 14
                )
                .CreateLogger()

        Log.Information("logging started, file: {LogPath}", logPath)
