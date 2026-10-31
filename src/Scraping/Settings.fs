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

    let workDir =
        configuration.GetValue<string>("work_dir", "work")

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
