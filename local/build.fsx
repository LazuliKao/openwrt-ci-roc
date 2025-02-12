#r "nuget: SSH.NET"
#r "nuget: YamlDotNet"

open Renci.SshNet
open System
open System.IO
open System.Text

let host = "192.168.6.110"
let username = "lk"
let password = "lk233"

let attachToStdout (shell: ShellStream) (top: int ref) (left: int ref) =
    let os = System.Console.OpenStandardOutput()

    shell.DataReceived.Add(fun data ->
        for b in data.Data do
            os.WriteByte b

        top.Value <- Console.CursorTop
        left.Value <- Console.CursorLeft)
// let reader = new System.IO.StreamReader(shell)
// let writer = new System.IO.StreamWriter(System.Console.OpenStandardOutput())

// task {
//     while shell.DataAvailable do
//         let! line = reader.ReadLineAsync()
//         if line <> null then
//             do! writer.WriteLineAsync line
// }
// |> Async.AwaitTask
// |> Async.StartImmediate
type YamlDict = Collections.Generic.Dictionary<Object, Object>
type YamlArray = Collections.Generic.List<Object>
let queue = new Collections.Concurrent.ConcurrentQueue<(string * (string array))>()


let processNext (shell: ShellStream) (pwd: string) =
    let (!>) cmd = shell.WriteLine cmd

    match queue.TryDequeue() with
    | true, (name, run) ->
        printfn "name: %s" name

        for line in run do
            printfn "%s" line

        let regex = RegularExpressions.Regex @"echo ""(.*)""\s?>>\s?\$GITHUB_ENV"


        let all =
            run
            |> Seq.map (fun line ->
                let m = regex.Match line

                if m.Success then
                    let text = m.Groups.[1].Value
                    sprintf "export %s" text
                else
                    line)
            |> String.concat "\n"

        !>(sprintf "echo \"%s\"" name)
        let tempScript = $"{pwd}/build_openwrt/tmp_scripts"
        !>(sprintf "mkdir -p %s" tempScript)
        let stageScript = $"{tempScript}/build_openwrt_{queue.Count}.sh"
        let cd_build_root = "cd " + pwd + "/" + "build_openwrt"
        !>(sprintf "cat <<EOF00 > %s\n#!/bin/bash\n%s\n%s\nEOF00" stageScript cd_build_root (all.Replace("$", "\\$")))
        !>("chmod +x " + stageScript)
        !>cd_build_root
        Some("source " + stageScript)
    | _ -> None

let runAll (shell: ShellStream) (pwd: string) =
    let (!>) cmd = shell.WriteLine cmd
    let full = StringBuilder()

    while queue.IsEmpty |> not do
        match processNext shell pwd with
        | Some cmd -> full.AppendLine cmd |> ignore
        | None -> ()

    let all = full.ToString().Replace("\r", "")
    let tempScript = $"{pwd}/build_openwrt/tmp_scripts"
    let stageScript = $"{tempScript}/build_openwrt_all.sh"
    !>(sprintf "cat <<EOF00 > %s\n#!/bin/bash\n%s\nEOF00" stageScript (all.Replace("$", "\\$")))
    !>("chmod +x " + stageScript)


let init (shell: ShellStream) =
    let (!>) cmd = shell.WriteLine cmd
    //enter screen
    !>"""if screen -list | grep -wq "\.build"; then
    screen -r build
else
    screen -S build
fi
"""

    !>"export DEBIAN_FRONTEND=noninteractive"

    let workflow = "Arthur&Athena-Master.yml"
    // let workflow = "Arthur&Athena-6.12.yml"
    let repo = Path.GetFullPath ".."
    let targetWorkflow = Path.Combine(repo, ".github", "workflows", workflow)
    let yaml = File.ReadAllText targetWorkflow
    let yamlObj = YamlDotNet.Serialization.Deserializer().Deserialize yaml
    let yamlObj = yamlObj :?> YamlDict
    let env = yamlObj.["env"] :?> YamlDict

    for kv in env do
        let k = kv.Key.ToString()
        let v = kv.Value.ToString()
        !>(sprintf "export %s=\"%s\"" k v)

    let jobs = yamlObj.["jobs"] :?> YamlDict

    for job in jobs do
        let job = job.Value :?> YamlDict
        //Collections.Generic.Dictionary<System.Object,System.Object>
        let steps = job.["steps"] :?> YamlArray

        for step in steps do
            let step = step :?> YamlDict
            let name = step.["name"].ToString()
            printfn "name: %s" name

            if step.ContainsKey "run" then
                let run =
                    step.["run"].ToString().Split '\n'
                    |> Seq.map (fun s -> s.Trim())
                    |> Seq.filter (fun s -> String.IsNullOrWhiteSpace s |> not)

                queue.Enqueue(name, run |> Seq.toArray)
// !>(sprintf "echo %s" name)
// !>(sprintf "%s" run)
// let steps = job.["steps"] :?> YamlDict

// for step in steps do
//     let step = step.Value :?> YamlDict
//     let run = step.["run"] :?> YamlDict

//     for run in run do
//         let run = run.Value :?> YamlDict
//         let name = run.["name"].ToString()
//         // let run = run.["run"].ToString()
//         !>(sprintf "echo %s" name)
//         // !>(sprintf "%s" run)
// let env= yamlObj
// let env = env.Children |> Seq.map (fun kv -> kv.Key.ToString(), kv.Value.ToString()) |> Map.ofSeq

let syncFiles (pwd: string) =
    use sftpClient = new SftpClient(host, username, password)
    sftpClient.Connect()
    printfn "syncing files..."
    let repo = Path.GetFullPath ".."
    printfn "repo: %s" repo
    let remote = pwd + "/" + "build_openwrt"

    if sftpClient.Exists remote |> not then
        sftpClient.CreateDirectory remote

    sftpClient.ChangeDirectory remote
    let files = Directory.GetFiles(repo, "*", SearchOption.AllDirectories)

    for file in files do
        let relativePath = Path.GetRelativePath(repo, file)

        let uploadFile target =
            use fs = File.OpenRead file
            let ext = Path.GetExtension file

            if ext = ".sh" || ext = ".config" || ext = ".yml" then
                use removeCrl = new StreamReader(fs)
                let text = removeCrl.ReadToEnd().Replace("\r", "")
                use fs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes text)
                sftpClient.UploadFile(fs, target, true)
            else
                sftpClient.UploadFile(fs, target, true)

        if relativePath.StartsWith ".git" |> not then
            let target = Path.Combine(remote, relativePath).Replace("\\", "/")
            let lastWriteLocal = File.GetLastWriteTimeUtc file

            if sftpClient.Exists target |> not then
                let parent = (Path.GetDirectoryName target).Replace("\\", "/")

                if not (sftpClient.Exists parent) then
                    printfn "creating %s" parent
                    sftpClient.CreateDirectory parent

                printfn "uploading %s" target
                uploadFile target
                sftpClient.SetLastWriteTime(target, lastWriteLocal)
            else
                let lastWriteRemote = sftpClient.GetLastWriteTime target

                if lastWriteLocal >= lastWriteRemote then
                    printfn "updating %s" target
                    uploadFile target
                    sftpClient.SetLastWriteTime(target, lastWriteLocal)
// printfn "update: %s" target
// if not (sftpClient.Exists dir) then
// sftpClient.CreateDirectory dir
// sftpClient.UploadFile(IO.File.OpenRead file, target)

let processCommand (shell: ShellStream) (pwd: string) (cmd: string) =
    match cmd with
    | "sync" ->
        syncFiles pwd
        false
    | "all" ->
        runAll shell pwd
        false
    | "next" ->
        processNext shell pwd |> ignore
        false
    | "skip" ->
        queue.TryDequeue() |> ignore
        false
    | _ -> true

let main () =
    use sshClient = new SshClient(host, username, password)
    sshClient.Connect()
    let pwd = (sshClient.RunCommand "pwd").Result.Trim()
    printfn "pwd: %s" pwd

    let shell =
        let columns = Console.WindowWidth |> uint
        let rows = Console.WindowHeight |> uint
        let width = Console.WindowWidth |> uint
        let height = Console.WindowHeight |> uint
        let bufferSize = 1024

        sshClient.CreateShellStream("screen", columns, rows, width, height, bufferSize)

    let mutable cursorTop = ref Console.CursorTop
    let mutable cursorLeft = ref Console.CursorLeft
    attachToStdout shell cursorTop cursorLeft // |> Async.AwaitTask |> Async.StartImmediate
    let (!>) cmd = shell.WriteLine cmd
    !>("cd " + pwd + "/" + "build_openwrt")
    !>(sprintf "export GITHUB_WORKSPACE=\"%s\"" (pwd + "/" + "build_openwrt"))
    init shell
    // let is = System.Console.OpenStandardInput()
    //重定向控制字符

    Console.CancelKeyPress.Add(fun e ->
        if shell.CanWrite then
            shell.Write "\x03"
            e.Cancel <- true)

    let tryWriteLine cmd =
        if shell.CanWrite then
            Console.CursorTop <- max 0 (Console.CursorTop - 1)
            Console.CursorLeft <- max 0 (min cursorLeft.Value (Console.WindowWidth - 1))
            shell.WriteLine cmd

    while shell.CanRead && shell.CanWrite && sshClient.IsConnected && shell.DataAvailable do
        let line = stdin.ReadLine()

        if processCommand shell pwd line then
            tryWriteLine line
        else
            tryWriteLine ""

    // let b = Console.Read()

    // if b = 13 then
    //     ()
    // elif b = 10 then
    //     shell.Write "\n"
    //     Console.CursorTop <- max 0 (Console.CursorTop - 1)
    //     Console.CursorLeft <- min cursorLeft.Value (Console.WindowWidth - 1)
    // else
    //     shell.WriteByte(byte b)
    //     shell.Flush()

    printfn "done"

main ()
