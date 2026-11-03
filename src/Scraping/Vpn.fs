namespace Scraping

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks
open Serilog

/// Rotates AmneziaWG tunnels so YouTube sees a different exit IP.
///
/// `AmneziaWG.exe /installtunnelservice <conf>` and
/// `/uninstalltunnelservice <tunnel>` both need administrator rights, so the
/// scraper must be started elevated for rotation to work. When it is not, every
/// call fails with "Access is denied" and the scanner simply keeps going on the
/// current IP — rotation is an optimisation, never a requirement.
module Vpn =

    /// Name of the tunnel currently installed by us, persisted so a restart
    /// still knows what to tear down.
    let private statePath = Path.Combine(Settings.workDir, "vpn_state.txt")

    let private readState () =
        try
            if File.Exists statePath then
                let s = File.ReadAllText(statePath).Trim()
                if s = "" then None else Some s
            else
                None
        with _ ->
            None

    let private writeState (name: string option) =
        try
            Directory.CreateDirectory Settings.workDir |> ignore
            File.WriteAllText(statePath, defaultArg name "")
        with _ ->
            ()

    let private mutableState = ref (readState ())

    /// All `.conf` files in the configured directory, sorted for determinism.
    let configs () : string list =
        let dir = YtDlp.resolvePath Settings.vpnConfigsDir

        if not (Directory.Exists dir) then
            []
        else
            Directory.GetFiles(dir, "*.conf")
            |> Array.sort
            |> List.ofArray

    let tunnelName (conf: string) = Path.GetFileNameWithoutExtension conf

    let current () = mutableState.Value

    let private run (args: string) : Task<int * string> =
        task {
            try
                let psi =
                    ProcessStartInfo(
                        FileName = Settings.vpnExe,
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
                let! out = proc.StandardOutput.ReadToEndAsync()
                let! err = proc.StandardError.ReadToEndAsync()
                proc.WaitForExit()
                return proc.ExitCode, (out + " " + err).Trim()
            with e ->
                return -1, e.Message
        }

    let disconnect (name: string) : Task<Result<unit, string>> =
        task {
            let! code, output = run (sprintf "/uninstalltunnelservice \"%s\"" name)

            if code = 0 then
                if mutableState.Value = Some name then
                    mutableState.Value <- None
                    writeState None

                return Ok()
            else
                return Error output
        }

    let connect (conf: string) : Task<Result<string, string>> =
        task {
            let name = tunnelName conf
            let! code, output = run (sprintf "/installtunnelservice \"%s\"" conf)

            if code = 0 then
                mutableState.Value <- Some name
                writeState (Some name)
                return Ok name
            else
                return Error output
        }

    /// Tear down the current tunnel (if any) and bring up the next config.
    /// Returns the new tunnel name, or the reason it could not be switched.
    let rotate () : Task<Result<string, string>> =
        task {
            let all = configs ()

            if all.IsEmpty then
                return Error(sprintf "no .conf files in %s" Settings.vpnConfigsDir)
            else
                match mutableState.Value with
                | Some name ->
                    let! _ = disconnect name
                    ()
                | None -> ()

                // next config after the one we were on, wrapping around
                let next =
                    match mutableState.Value with
                    | Some name ->
                        let idx = all |> List.tryFindIndex (fun c -> tunnelName c = name)

                        match idx with
                        | Some i -> all.[(i + 1) % all.Length]
                        | None -> all.Head
                    | None -> all.Head

                let! result = connect next

                match result with
                | Ok name ->
                    Log.Information("vpn: switched to {Tunnel}", name)
                    do! Http.delay Settings.vpnSettleMs
                    return Ok name
                | Error reason ->
                    Log.Warning("vpn: could not switch to {Conf} — {Reason}", next, reason)
                    return Error reason
        }

    /// True when the process can actually install a tunnel. Cheap check: try to
    /// open the service control manager is not available from .NET without
    /// P/Invoke, so we just report whether the exe and configs exist and let the
    /// first rotate attempt reveal the permission problem.
    let available () =
        File.Exists Settings.vpnExe && not (configs ()).IsEmpty
