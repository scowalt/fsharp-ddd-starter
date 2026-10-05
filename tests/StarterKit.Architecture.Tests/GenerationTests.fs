namespace StarterKit.Architecture.Tests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Xml.Linq
open Xunit
open StarterKit.WorkTracking.Infrastructure

module private GenerationFixture =
    let run cwd program arguments =
        let start =
            ProcessStartInfo(
                program,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment.Remove("GIT_DIR") |> ignore
        start.Environment.Remove("GIT_WORK_TREE") |> ignore
        start.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"
        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(120000)) then
            child.Kill(true)
            failwith "Generated fixture command exceeded its deadline."

        Assert.True(
            System.Threading.Tasks.Task.WaitAll(
                [| stdout :> System.Threading.Tasks.Task
                   stderr :> System.Threading.Tasks.Task |],
                5000
            ),
            "Fixture command retained output pipes after exit."
        )

        Assert.True((child.ExitCode = 0), stdout.Result + stderr.Result)

    let copyTree source destination =
        for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(source, file)
            let parts = relative.Split(Path.DirectorySeparatorChar)

            if
                not (
                    parts
                    |> Array.exists (fun part -> part = "bin" || part = "obj" || part = ".git")
                )
            then
                let target = Path.Combine(destination, relative)
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(file, target)

type GenerationTests() =
    [<Fact>]
    member _.``generation fixes logical context identity once and assembly namespace refactors do not reapply SQL``() =
        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

        if File.Exists(Path.Combine(root, "scripts", "init.sh")) then
            let fixture =
                Path.Combine(Path.GetTempPath(), $"migration-generation-{Guid.NewGuid():N}")

            Directory.CreateDirectory fixture |> ignore

            try
                for directory in [ "src"; "tests"; "scripts"; ".config"; "docs" ] do
                    GenerationFixture.copyTree (Path.Combine(root, directory)) (Path.Combine(fixture, directory))

                for file in
                    [ "Directory.Build.props"
                      "Directory.Packages.props"
                      "global.json"
                      "StarterKit.slnx"
                      ".gitattributes"
                      ".template-manifest" ] do
                    if File.Exists(Path.Combine(root, file)) then
                        File.Copy(Path.Combine(root, file), Path.Combine(fixture, file))

                GenerationFixture.run fixture "bash" [ "scripts/init.sh"; "Workshop"; "Enrollment" ]

                let project =
                    "src/Workshop.Enrollment.Infrastructure/Workshop.Enrollment.Infrastructure.fsproj"

                GenerationFixture.run
                    fixture
                    "dotnet"
                    [ "build"; project; "--configuration"; "Release"; "--no-restore"; "--nologo" ]

                let originalAssembly =
                    Assembly.LoadFile(
                        Path.Combine(
                            fixture,
                            "src/Workshop.Enrollment.Infrastructure/bin/Release/net10.0/Workshop.Enrollment.Infrastructure.dll"
                        )
                    )

                let path = Path.Combine(fixture, "proof.db")
                let connectionString = $"Data Source={path};Pooling=False"

                Assert.Equal(
                    Ok { Applied = [ "enrollment/001_initial.sql" ] },
                    SqliteMigrations.run connectionString (Embedded originalAssembly)
                )

                Assert.False(File.Exists(Path.Combine(fixture, "scripts", "init.fsx")))
                let sourceRoot = Path.Combine(fixture, "src")

                for file in Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories) do
                    if not (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) then
                        let text = File.ReadAllText file
                        Assert.DoesNotContain("StarterKit", text)
                        Assert.DoesNotContain("WorkTracking", text)
                        File.WriteAllText(file, text.Replace("Workshop.Enrollment", "Reorganized.Records"))

                for file in Directory.EnumerateFiles(sourceRoot, "*.fsproj", SearchOption.AllDirectories) do
                    let projectXml = XDocument.Load file

                    projectXml.Root.AddFirst(
                        XElement(
                            XName.Get "PropertyGroup",
                            XElement(XName.Get "AssemblyName", "Renamed." + Path.GetFileNameWithoutExtension(file)),
                            XElement(XName.Get "RootNamespace", "Reorganized.Records")
                        )
                    )

                    projectXml.Save file

                GenerationFixture.run fixture "dotnet" [ "build"; project; "--configuration"; "Release"; "--nologo" ]

                let renamedAssembly =
                    Assembly.LoadFile(
                        Path.Combine(
                            fixture,
                            "src/Workshop.Enrollment.Infrastructure/bin/Release/net10.0/Renamed.Workshop.Enrollment.Infrastructure.dll"
                        )
                    )

                Assert.NotEqual<string>(originalAssembly.GetName().Name, renamedAssembly.GetName().Name)
                Assert.Equal(Ok { Applied = [] }, SqliteMigrations.run connectionString (Embedded renamedAssembly))
            finally
                Directory.Delete(fixture, true)
