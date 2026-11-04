namespace Scraping

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Serilog

/// Rotates AmneziaWG tunnels so YouTube sees a different exit IP.
///
/// The source of truth is `awg show interfaces`, never our own bookkeeping: the
/// tunnel may have been installed by the GUI or by hand before we started.
/// Two active interfaces at once break routing completely (both claim
/// 0.0.0.0/0), so every operation first tears down whatever is up and only then
/// installs one tunnel.
///
/// `AmneziaWG.exe /installtunnelservice` needs administrator rights; when the
/// scraper is not elevated the calls fail with "Access is denied" and the crawl
/// simply continues on the current IP — rotation is an optimisation, never a
/// requirement.
module Vpn =

    /// `awg.exe` lives next to `AmneziaWG.exe`.
    let private awgExe =
        let dir = Path.GetDirectoryName Settings.vpnExe
        if String.IsNullOrEmpty dir then "awg.exe" else Path.Combine(dir, "awg.exe")

    let private exec (exe: string) (args: string) : int * string =
        try
            let psi =
                ProcessStartInfo(
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                )

            use proc = new Process(StartInfo = psi)
            proc.Start() |> ignore
            let err = proc.StandardError.ReadToEndAsync()
            let out = proc.StandardOutput.ReadToEnd()
            let err = err.Result
            proc.WaitForExit()
            proc.ExitCode, (out + " " + err).Trim()
        with e ->
            -1, e.Message

    /// All `.conf` files in the configured directory, sorted for determinism.
    let configs () : string list =
        let dir = YtDlp.resolvePath Settings.vpnConfigsDir

        if not (Directory.Exists dir) then
            []
        else
            Directory.GetFiles(dir, "*.conf") |> Array.sort |> List.ofArray

    let tunnelName (conf: string) = Path.GetFileNameWithoutExtension conf

    /// Interfaces that are up right now, straight from `awg show interfaces`.
    let active () : string list =
        if not (File.Exists awgExe) then
            []
        else
            let code, output = exec awgExe "show interfaces"

            if code <> 0 then
                []
            else
                output.Split([| '\r'; '\n'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s <> "")
                |> List.ofArray

    let current () = active () |> List.tryHead

    let available () = File.Exists Settings.vpnExe && not (configs ()).IsEmpty

    let disconnect (name: string) : bool =
        let code, output = exec Settings.vpnExe (sprintf "/uninstalltunnelservice \"%s\"" name)

        if code = 0 then
            Log.Information("vpn: disconnected {Tunnel}", name)
            true
        else
            Log.Warning("vpn: could not disconnect {Tunnel} — {Reason}", name, output)
            false

    /// Spin until `cond` holds. The tunnel service is asynchronous — both
    /// removal and installation take a moment — so every state change is
    /// polled, never assumed after a fixed sleep.
    let private until (timeoutMs: int) (cond: unit -> bool) : bool =
        let sw = Stopwatch.StartNew()
        let mutable ok = cond ()

        while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
            Thread.Sleep 200
            ok <- cond ()

        ok

    /// Tear down every active interface, retrying once if the service lags.
    /// Returns how many are still up afterwards (0 is what we want). The
    /// remote session rides on the tunnel, so the caller must be a process
    /// that outlives it — see work/switch-vpn.ps1.
    let disconnectAll () : int =
        let names = active ()

        for name in names do
            disconnect name |> ignore

        if not names.IsEmpty && not (until 10000 (fun () -> (active ()).IsEmpty)) then
            Log.Warning("vpn: interfaces survived uninstall, forcing again: {Names}", String.concat ", " (active ()))

            for name in active () do
                disconnect name |> ignore

            until 10000 (fun () -> (active ()).IsEmpty) |> ignore

        let remaining = active ()

        if remaining.Length > 1 then
            Log.Warning("vpn: {N} interfaces still active: {Names}", remaining.Length, String.concat ", " remaining)

        remaining.Length

    /// Install one tunnel. Refuses to install while another interface is up:
    /// two default routes break all connectivity.
    let connect (conf: string) : Result<string, string> =
        let left = disconnectAll ()

        if left > 0 then
            Error(sprintf "%d interface(s) still active, refusing to install a second one" left)
        else
            let name = tunnelName conf

            let install () =
                let code, output = exec Settings.vpnExe (sprintf "/installtunnelservice \"%s\"" conf)
                if code <> 0 then Log.Warning("vpn: install {Tunnel} failed — {Reason}", name, output)
                code

            let code = install ()

            // a leftover service with the same name makes the install fail;
            // clear it and try once more
            let code =
                if code = 0 then
                    0
                else
                    exec Settings.vpnExe (sprintf "/uninstalltunnelservice \"%s\"" name) |> ignore
                    Thread.Sleep 1000
                    install ()

            if code <> 0 then
                Error(sprintf "could not install %s" name)
            elif not (until (max Settings.vpnSettleMs 15000) (fun () -> List.contains name (active ()))) then
                Error "tunnel installed but did not appear in `awg show interfaces`"
            else
                // routes are programmed asynchronously as well
                Thread.Sleep Settings.vpnSettleMs

                let up = active ()

                // never leave two default routes behind, whatever happened
                for extra in up |> List.filter (fun n -> n <> name) do
                    disconnect extra |> ignore

                if List.contains name (active ()) then
                    Log.Information("vpn: connected {Tunnel}", name)
                    Ok name
                else
                    Error "tunnel vanished right after installation"

    /// Switch to the next config after whichever interface was up.
    let rotate () : Task<Result<string, string>> =
        Task.FromResult(
            let all = configs ()

            if all.IsEmpty then
                Error(sprintf "no .conf files in %s" Settings.vpnConfigsDir)
            else
                let before = active ()

                if before.Length > 1 then
                    Log.Warning(
                        "vpn: {N} interfaces were active ({Names}) — tearing all down",
                        before.Length,
                        String.concat ", " before
                    )

                let previous = before |> List.tryHead

                let next =
                    match previous with
                    | Some p ->
                        match all |> List.tryFindIndex (fun c -> tunnelName c = p) with
                        | Some i -> all.[(i + 1) % all.Length]
                        | None -> all.Head
                    | None -> all.Head

                connect next
        )
