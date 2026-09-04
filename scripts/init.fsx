open System
open System.IO
open System.Text
open System.Text.RegularExpressions

let fail message =
    eprintfn "%s" message
    Environment.ExitCode <- 64
    exit 64

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--")

if args.Length <> 2 then
    fail "Expected an application name and a bounded-context name."

let applicationName = args[0]
let contextName = args[1]
let validName = Regex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)

for label, value in [ "Application name", applicationName; "Bounded-context name", contextName ] do
    if not (validName.IsMatch value) then
        fail $"{label} '{value}' is invalid. Use a PascalCase .NET identifier."

if applicationName = contextName then
    fail "Application and bounded-context names must be different."

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let replacements =
    [ "StarterKit.WorkTracking", $"{applicationName}.{contextName}"
      "StarterKit", applicationName
      "starterkit", applicationName.ToLowerInvariant()
      "WorkTracking", contextName
      "worktracking", contextName.ToLowerInvariant() ]

let excluded path =
    let relative = Path.GetRelativePath(root, path)

    let parts =
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    parts
    |> Array.exists (fun part -> part = ".git" || part = "bin" || part = "obj")
    || relative.Contains($"wwwroot{Path.DirectorySeparatorChar}vendor")

let replaceText (value: string) =
    replacements
    |> List.fold
        (fun (current: string) (oldValue: string, newValue: string) -> current.Replace(oldValue, newValue))
        value

for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
    if
        not (excluded file)
        && Path.GetFullPath(file) <> Path.GetFullPath(__SOURCE_FILE__)
    then
        let bytes = File.ReadAllBytes file

        if not (bytes |> Array.contains 0uy) then
            let original = Encoding.UTF8.GetString bytes
            let updated = replaceText original

            if updated <> original then
                File.WriteAllText(file, updated, UTF8Encoding(false))

let paths =
    seq {
        yield! Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        yield! Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
    }
    |> Seq.filter (excluded >> not)
    |> Seq.sortByDescending (fun path -> path.Length)
    |> Seq.toArray

for path in paths do
    let parent = Path.GetDirectoryName path
    let oldName = Path.GetFileName path
    let newName = replaceText oldName

    if oldName <> newName then
        let destination = Path.Combine(parent, newName)

        if File.Exists path then
            File.Move(path, destination)
        elif Directory.Exists path then
            Directory.Move(path, destination)
