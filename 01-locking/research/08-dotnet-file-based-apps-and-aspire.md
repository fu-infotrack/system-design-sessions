# .NET File-Based Apps (`dotnet run app.cs`) and the Single-File Aspire AppHost

## Summary

**Verdict: yes, this is a workable setup today, and I verified it end-to-end on this machine.** File-based apps are GA in the .NET 10 SDK, multi-file support shipped via `#:include` in SDK 10.0.300+ (contrary to the preview-era "not coming to .NET 10" messaging), and Aspire 13 ships a single-file AppHost (`apphost.cs`) as the *default* output of `aspire new`. The key directive syntax is `#:package Name@Version`, `#:sdk Name@Version`, `#:property Name=Value`, `#:project path`, `#:include glob` — `@` separates package/SDK versions, `=` separates property name and value, and there is exactly one space after the colon-prefixed keyword. I built and ran a single-file Aspire AppHost that provisioned real PostgreSQL and Redis containers and passed connection strings to a *file-based* `.cs` worker via the (experimental) `AddCSharpApp` API — the worker connected to Postgres with Npgsql and executed a query. The sharp edges are: `AddCSharpApp` is experimental and needs a `#pragma warning disable ASPIRECSHARPAPPS001`, native AOT is on by default (which disables Hot Reload and breaks some packages), and Aspire's Redis connection string now carries `ssl=true`.

**Environment used for verification:** .NET SDK **10.0.302** (runtime 10.0.10), Aspire CLI **13.3.5**, Aspire packages **13.5.3**, Docker 29.7.2, Debian 13 on WSL2.

---

## Findings

### 1. The basics

**Command.** `dotnet run app.cs` works. Verified:

```
$ dotnet run hello.cs
Hello from file-based app, args:
```

Three equivalent invocation forms are documented and all three were verified working:
`dotnet run file.cs`, `dotnet run --file file.cs`, and the shorthand `dotnet file.cs`.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

Note the backwards-compatibility rule: *"When a project file exists in the current working directory, `dotnet run file.cs` without the `--file` option runs that project and passes `file.cs` as an argument to the target app."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**SDK version required.** .NET 10 SDK or later. The Learn page is stamped *"This article applies to: ✔️ .NET 10 SDK and later versions"*. `#:include` specifically requires **SDK 10.0.300 or later**.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**`Main` vs top-level statements.** Top-level statements are the expected style, and it is what every official example uses. Verified: a file containing only `Console.WriteLine(...)` runs. Included files (via `#:include`) may **not** add top-level statements — *"These files can add types, methods, namespaces, and other declarations, but they cannot add top-level statements."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**Shebang.** The current documented form is:

```
#!/usr/bin/env -S dotnet --
```

then `chmod +x file.cs`, then `./file.cs`. The `--` matters: it stops `dotnet` consuming arguments that match its own CLI parameters. The `-S` flag lets `env` split the remaining text into separate arguments.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

The docs give a fallback: *"If `-S` isn't supported on your system, use `#!/usr/bin/env dotnet` instead. With this shebang, `dotnet` might consume arguments that match its own CLI parameters."*

**I verified this difference empirically** — it is real and it will bite you:

```
$ ./mytool --help          # shebang: #!/usr/bin/env -S dotnet --
extensionless ok: --help   # arg reached the app

$ ./plain.cs --help        # shebang: #!/usr/bin/env dotnet
Description:
  .NET Run Command         # arg was swallowed by the dotnet CLI
```

Docs also require **LF line endings and no BOM** when using a shebang.

**Does the file need a `.cs` extension?** **No** — for shell execution. Verified: a file named `mytool` (no extension) with the shebang runs directly via `./mytool`, *and* `dotnet run mytool` works too:

```
$ ./mytool a b
extensionless ok: a,b
$ dotnet run mytool
extensionless ok:
```

This matches the .NET 10 SDK release notes: *"The `.cs` file extension can be omitted, allowing for direct execution of extensionless C# files configured with a shebang."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/sdk>

### 2. The directives — exact syntax

The general grammar from the SDK design doc is `#:kind name[separator value]`, with *"Leading and trailing white space is not considered part of the name and value."*
Source: <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>

There are **seven** directive kinds. Five are supported; two are gated behind experimental MSBuild properties. I confirmed the set empirically by feeding the SDK an unknown directive:

```
$ dotnet run bad.cs
/…/bad.cs(1): error: Unrecognized directive 'bogus'.
```

| Directive | Exact form | Separator | Status (SDK 10.0.302, verified) |
|---|---|---|---|
| `#:package` | `#:package Npgsql@9.*` | `@` | Supported |
| `#:sdk` | `#:sdk Microsoft.NET.Sdk.Web` / `#:sdk Aspire.AppHost.Sdk@13.5.3` | `@` | Supported |
| `#:property` | `#:property LangVersion=preview` | `=` | Supported |
| `#:project` | `#:project ../SharedLibrary/SharedLibrary.csproj` | (path only) | Supported |
| `#:include` | `#:include helpers.cs` | (path/glob) | Supported (SDK 10.0.300+) |
| `#:exclude` | `#:exclude skip.cs` | (path/glob) | Requires `ExperimentalFileBasedProgramEnableTransitiveDirectives=true` in some cases |
| `#:ref` | `#:ref lib.cs` | (path) | **Experimental**, gated |

**There is exactly one space after the directive keyword** (`#:package` then space then the value). There is **no space around the `@`** and **no space around the `=`**.

#### `#:package`

```csharp
#:package Newtonsoft.Json
#:package Serilog@3.1.1
#:package Spectre.Console@*
```
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

The `@` **is** the separator. Injects `<PackageReference Include="{name}" Version="{value}" />`.
Source: <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>

**Floating / wildcard versions are supported.** I tested four forms:

```
#:package Humanizer@2.14.1      -> works (exact pin)
#:package Humanizer@2.*         -> works (floating)
#:package Humanizer@*           -> works (latest)
#:package Humanizer@[2.14.1,3)  -> works (NuGet range syntax)
#:package Humanizer             -> FAILS: error NU1015: The following PackageReference
                                   item(s) do not have a version specified: Humanizer
```

So **`#:package Npgsql@9.*` is valid**, and so is `#:package Npgsql@9.0.3`. Omitting the version only works under central package management with a `Directory.Packages.props`.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

#### `#:sdk`

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:sdk Aspire.AppHost.Sdk@13.0.2
```
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

Defaults to `Microsoft.NET.Sdk` when absent. **Yes, a version can be pinned with `@`.** Semantics from the design doc: the *first* `#:sdk` becomes `<Project Sdk="{name}/{value}">`; *subsequent* ones become `<Sdk Name="{name}" Version="{value}" />`.
Source: <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>

Verified `#:sdk Microsoft.NET.Sdk.Web` builds an ASP.NET Core minimal-API file-based app.

#### `#:property`

```csharp
#:property TargetFramework=net10.0
#:property PublishAot=false
```
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**`=` is the separator** — `#:property LangVersion=preview` is correct. (The preview-era space-separated form is dead; see §"Preview → GA changes".) Verified `#:property LangVersion=preview` and `#:property PublishAot=false` both work.

MSBuild expressions are allowed in the value:
```csharp
#:property LogLevel=$([MSBuild]::ValueOrDefault('$(LOG_LEVEL)', 'Information'))
```

#### `#:project`

**It exists.** *"References another project file or directory that contains a project file."*
```csharp
#:project ../SharedLibrary/SharedLibrary.csproj
```
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

Verified working against a real `classlib`:
```
$ dotnet run app.cs      # app.cs has: #:project Lib/Lib.csproj
hi from project
```

#### Other directives

**`#:include`** — the multi-file mechanism, covered in §3.

**`#:exclude`** — *"Inverse of `#:include`; uses `Remove="{value}"` instead of `Include`."* Verified working:
```
$ dotnet run exd/main.cs   # #:include *.cs  +  #:exclude skip.cs
5                          # deliberately-broken skip.cs was excluded
```
Source: <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>

**`#:ref`** — references *other `.cs` files as separate library projects*. **Experimental and gated.** Observed error verbatim:
```
/…/reft/main.cs(2): error: This is an experimental feature, set MSBuild property
'ExperimentalFileBasedProgramEnableRefDirective' to 'true' to enable it.
```
Enabling it via `#:property ExperimentalFileBasedProgramEnableRefDirective=true` then produced `error CS5001: Program does not contain a static 'Main' method suitable for an entry point`. **Do not use `#:ref` for the demos** — use `#:include`.

**TFM / framework.** There is no dedicated framework directive. Use `#:property TargetFramework=net10.0`.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

### 3. Multi-file support — CURRENT state

**Multi-file works today**, via `#:include`. This changed after the previews: the .NET 10 preview blog posts said multi-file was *not* coming to .NET 10 and was pushed to .NET 11 (<https://github.com/dotnet/sdk/issues/48174>, per <https://andrewlock.net/exploring-dotnet-10-preview-features-1-exploring-the-dotnet-run-app.cs/>). It landed in **SDK 10.0.300**.

Docs note: *"This directive is available in .NET 11 Preview 3 and .NET SDK 10.0.300 and later."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

Supported forms — literal paths, glob patterns, and MSBuild properties:
```csharp
#:include helpers.cs
#:include models/customer.cs
#:include shared/**/*.cs
#:include $(MSBuildProjectName).*.cs
```

Extension → item-type mapping: `*.cs` → `Compile`, `*.resx` → `EmbeddedResource`, `*.json` → `None`, `*.razor` → `Content`. The design doc adds `.dll` → `Reference` and says this is customisable via the `FileBasedProgramsItemMapping` property (default `".cs=Compile;.resx=EmbeddedResource;.json=None;.razor=Content;.dll=Reference"`).
Sources: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>, <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>

**Verified — sibling file:**
```
$ dotnet run multi/main.cs     # main.cs: #:include helpers.cs
Hi world
```
**Verified — recursive glob:**
```
$ dotnet run multi2/main.cs    # main.cs: #:include shared/**/*.cs
42
```

**Two constraints that matter:**

1. **Included `.cs` files cannot contain top-level statements** — only types/methods/namespaces. Only the entry-point file has top-level statements.
2. **Glob patterns disable build caching.** *"When you use glob patterns, file-based app build caching is currently disabled."* Prefer explicit `#:include foo.cs` lines for the demos.

**There is no directory-based convention** — a bare sibling `.cs` file is *not* picked up automatically. You must name it in an `#:include`.

**New analyzer: CA2266.** As soon as you use `#:include`, the SDK warns if the entry point lacks a shebang. Observed verbatim:
```
/…/multi3/prog.cs(2,1): warning CA2266: File-based program entry point should start
with '#!' (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2266)
```
Cause: *"Your entry point file in a multi-file file-based program doesn't start with a shebang (`#!`) line."* Rationale: *"to clearly distinguish it from files brought in with `#:include` or `#:ref`."* Enabled by default as a warning in .NET 10. Adding the shebang silences it (verified). Suppress with `#:property NoWarn=$(NoWarn);CA2266`.
Source: <https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2266>

### 4. Converting to a project

Exact command (verified against `dotnet project convert --help` locally):

```
dotnet project convert <file>
```

Options: `-o, --output`, `--force`, `--interactive`, `--dry-run`, `--delete-source`.

*"This command makes a copy of the `.cs` file and creates a `.csproj` file with equivalent SDK items, properties, and package references based on the original file's `#:` directives. Both files are placed in a directory named for the application next to the original `.cs` file, which is left untouched."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**Verified.** Converting a file whose only directive was `#:package Humanizer@2.14.1` produced `pkg/pkg.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <PackAsTool>true</PackAsTool>
    <UserSecretsId>pkg-3c20bf58…</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Humanizer" Version="2.14.1" />
  </ItemGroup>
</Project>
```

This is also the clearest confirmation of the **implicit defaults**: `TargetFramework=net10.0`, `ImplicitUsings=enable`, `Nullable=enable`, **`PublishAot=true`**, **`PackAsTool=true`**.

### 5. Other mechanics

**Build artifact cache.** Docs say *"the build output goes to the system's temporary directory under `<temp>/dotnet/runfile/<appname>-<appfilesha>/bin/<configuration>/`"*.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**Observed on this Linux box it is NOT under `/tmp`** — it is under the local application data directory:
```
/home/fu/.local/share/dotnet/runfile/hello-4bbb2d8074…/bin/debug/hello.dll
/home/fu/.local/share/dotnet/runfile/dotnet-run-file-artifacts-metadata.json
```
(`TMPDIR` was unset.) So on Linux expect `~/.local/share/dotnet/runfile/`, on Windows `%LOCALAPPDATA%\dotnet\runfile\`. Treat the docs' `<temp>` wording as approximate.

Cache key = source file content + directive configuration + SDK version + implicit build file existence/content. Clear with `dotnet clean file-based-apps` (add `--days N`, default 30) or `dotnet clean file.cs`.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**Passing arguments.** Yes — `dotnet run file.cs -- arg1 arg2`. Verified:
```
$ dotnet run hello.cs -- a b c
Hello from file-based app, args: a,b,c
```

**stdin.** `echo 'Console.WriteLine("hi");' | dotnet run -` is supported.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**`dotnet watch`.** **Works.** Verified `dotnet watch hello.cs`:
```
dotnet watch 🔥 Hot reload enabled. …
dotnet watch ⚠ [hello.cs (net10.0)] Project does not support Hot Reload: 'PublishAot'
             property is 'True'. Application will be restarted when updated.
             Set 'StartupHookSupport' project property to 'True' to enable Hot Reload.
dotnet watch ⌚ Waiting for changes
Hello from file-based app, args:
```
So watch+restart works out of the box; **Hot Reload does not**, because `PublishAot=true` is the default. Add `#:property PublishAot=false` (or `#:property StartupHookSupport=true`) to get real Hot Reload.

**Startup/perf.** Measured on this box:
```
cached run   (dotnet run hello.cs)             real  0m0.201s
cold rebuild (dotnet run hello.cs --no-cache)  real  0m0.852s
```
First-ever run additionally pays NuGet restore. Cached runs are effectively instant.

**Concurrency restriction.** *"Concurrent invocations of a file-based app … can cause errors due to contention over the build output files. To avoid this, first build the file-based app via `dotnet build file.cs` before starting the concurrent instances via `dotnet run file.cs --no-build`."*
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**Publish / pack.** `dotnet publish file.cs` → native-AOT executable in an `artifacts/` dir next to the `.cs`. `dotnet pack file.cs` → a .NET tool (`PackAsTool=true` by default).

**Launch profiles.** File-based apps use a flat `<AppName>.run.json` beside the source (e.g. `app.run.json`), instead of `Properties/launchSettings.json`. If both exist, the traditional location wins and the CLI logs a warning. Select with `--launch-profile` or `DOTNET_LAUNCH_PROFILE`.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

**User secrets.** Supported: `dotnet user-secrets set "ApiKey" "…" --file file.cs`. The `UserSecretsId` is a stable hash of the full file path — **so moving the file changes its secrets identity.**

**Layout restriction (important for a demo repo).** Do **not** put file-based apps inside a `.csproj` directory cone, and be aware `Directory.Build.props` / `Directory.Packages.props` / `nuget.config` / `global.json` in parent directories all apply.
Source: <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>

### 6. Aspire single-file AppHost — THE KEY QUESTION

**Yes, Aspire supports a single-file AppHost, and it is GA.** *"Aspire 13.0 introduces comprehensive support for single-file app hosts, allowing you to define your entire distributed application in a single `.cs` file without a project file."* It *"works seamlessly with `aspire run`, `aspire deploy`, `aspire publish`, `aspire add`, `aspire update`"* and requires *".NET 10.0 SDK or later"*.
Source: <https://aspire.dev/whats-new/aspire-13/>

Historical: it was tracked as <https://github.com/dotnet/aspire/issues/9612> ("Support single file cs apphost"), with CLI support in <https://github.com/dotnet/aspire/pull/11451>. During Aspire 9.5 / .NET 10 RC1 it was behind a feature flag (`aspire config set features.singlefileAppHostEnabled true`); that flag is **no longer needed in 13.x** — I confirmed `aspire config list` reports no configuration values and everything worked.

#### The EXACT set of directives

**It is ONE directive.** Not `#:sdk Microsoft.NET.Sdk` + `#:sdk Aspire.AppHost.Sdk`, and **not** a separate `#:package Aspire.Hosting.AppHost` — the SDK brings that in implicitly.

This is not a guess. I ran `aspire new aspire-empty` and the template emitted, verbatim:

```csharp
#:sdk Aspire.AppHost.Sdk@13.5.3

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
```

I then ran `aspire add postgres` and `aspire add redis` against that file, and **the Aspire CLI itself rewrote the directives** to exactly this (note it sorts `#:package` above `#:sdk`):

```csharp
#:package Aspire.Hosting.PostgreSQL@13.5.3
#:package Aspire.Hosting.Redis@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3
```

That is the authoritative form. I confirmed the SDK pulls in the AppHost package implicitly — building a file with only `#:sdk Aspire.AppHost.Sdk@13.5.3` loaded `~/.nuget/packages/aspire.hosting.apphost/13.5.3/build/Aspire.Hosting.AppHost.targets`.

For completeness I also verified the two alternative forms **build fine** but are unnecessary: `#:sdk Microsoft.NET.Sdk` + `#:sdk Aspire.AppHost.Sdk@13.5.3`, and adding an explicit `#:package Aspire.Hosting.AppHost@13.5.3`.

The docs also mention an optional property for a single-file AppHost:
```csharp
#:property AspireUseCliBundle=true
```
Source: <https://aspire.dev/get-started/aspire-sdk/>
Building with plain `dotnet build apphost.cs` without it emits `warning ASPIRE010: … is configured with AspireUseCliBundle=false. Some Aspire features require the Aspire CLI bundle.` Running under `aspire run` sets this itself, so you do **not** need the property if you launch via the CLI.

#### Version that introduced it

**Aspire 13.0.** Source: <https://aspire.dev/whats-new/aspire-13/>. Current stable at time of writing: **13.5.3** (confirmed against `api.nuget.org` flat-container index for `Aspire.Hosting.AppHost`, `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Redis`, `Aspire.AppHost.Sdk` — all `…13.5.0, 13.5.1, 13.5.2, 13.5.3`).

#### How to run it

**`aspire run` is the documented and preferred path** and it auto-discovers the file:
```
$ aspire run
Finding AppHosts...
apphost.cs
🛠️ Building AppHost... apphost.cs
   AppHost:  apphost.cs
 Dashboard:  https://localhost:17193/login?t=…
```

`dotnet run apphost.cs` also builds and starts the AppHost (verified), but `aspire run` additionally handles dev certs, the dashboard, the CLI bundle property, and the launch profile.

`aspire run` created an **`aspire.config.json`** next to the file — this is the single-file AppHost's launch configuration, and it points at the AppHost:
```json
{
  "appHost": { "path": "apphost.cs" },
  "profiles": {
    "https": { "applicationUrl": "https://localhost:17142;http://localhost:15257", … },
    "http":  { "applicationUrl": "http://localhost:15257", … }
  }
}
```

#### CLI commands that create one

- **`aspire new aspire-empty --language csharp`** — on Aspire 13.3.5 this produces a **single-file `apphost.cs` by default**, with no `.csproj` at all. Verified: the whole output tree was `apphost.cs`, `aspire.config.json`, `.vscode/`, `.agents/`.
- **`dotnet new aspire-apphost-singlefile`** — a dedicated template exists. Confirmed in `dotnet new list aspire`:
  ```
  Aspire Single-File App Host    aspire-apphost-singlefile    [C#]    Common/Aspire/AppHost/SingleFile
  ```
- **`aspire init`** — documented as able to *"create single-file AppHost for quick starts"* when no solution exists. Source: <https://aspire.dev/whats-new/aspire-13/>
- **`aspire add <integration>`** — works on a lone `apphost.cs` and edits the `#:package` directives in place (verified above).

#### Referencing OTHER file-based `.cs` apps as resources — YES

The API is **`AddCSharpApp`**, on `Aspire.Hosting.ProjectResourceBuilderExtensions`. It is **experimental**, carrying `[Experimental("ASPIRECSHARPAPPS001")]`, so C# AppHosts must suppress the diagnostic.
Sources: <https://aspire.dev/integrations/dotnet/csharp-file-based-apps/>, <https://learn.microsoft.com/en-us/dotnet/api/aspire.hosting.projectresourcebuilderextensions.addcsharpapp>

Documented shape:
```csharp
#pragma warning disable ASPIRECSHARPAPPS001
var builder = DistributedApplication.CreateBuilder(args);
builder.AddCSharpApp("worker", "../worker/Program.cs");
builder.Build().Run();
```

*"You call `AddCSharpApp` with a resource name and the relative path to a `.cs` file. If the path is not an absolute path then it will be computed relative to the app host directory."* It also accepts `.csproj` files or directories containing them. *"File-based apps integrate fully with Aspire's resource model … The file-based app receives connection strings and service discovery information through environment variables, exactly like projects added with `AddProject<T>`."*
Source: <https://aspire.dev/integrations/dotnet/csharp-file-based-apps/>

There is an `Action<ProjectResourceOptions>` overload supporting `LaunchProfileName`, `ExcludeLaunchProfile`, `ExcludeKestrelEndpoints`.

Tracking issue: <https://github.com/dotnet/aspire/issues/11498>

**`AddExecutable` also exists** as the generic fallback — `builder.AddExecutable("frontend", "node", ".", "server.js")`, chainable with `.WithReference(redis)` etc. Source: <https://aspire.dev/app-host/executable-resources/>. You do **not** need it for `.cs` apps; `AddCSharpApp` is the right tool.

#### END-TO-END VERIFICATION (this is the important part)

I ran the AppHost below. Results:

1. `aspire run` discovered `apphost.cs`, built it, and started the dashboard.
2. Real containers came up:
   ```
   postgres-uqxupzfv   postgres:18.3   Up About a minute
   cache-mvdkyfzh      redis:8.6       Up About a minute
   ```
3. The file-based worker was launched by Aspire as:
   ```
   dotnet run --file /…/ctl/worker/worker.cs --no-cache --configuration Debug --no-launch-profile
   ```
4. Its environment (read from `/proc/<pid>/environ`) contained:
   ```
   ConnectionStrings__appdb=Host=localhost;Port=37745;Username=postgres;Password=…;Database=appdb
   ConnectionStrings__cache=localhost:46019,password=…,ssl=true
   OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:21293
   ```
5. The worker — which declared its own `#:package Npgsql@9.*` — actually connected and queried:
   ```
   pg   = Host=localhost;Port=35599;Username=postgres;Password=…;Database=appdb
   pg query -> 1
   ```

So: **single-file AppHost + Postgres + Redis + a file-based `.cs` worker with its own NuGet packages, wired by connection string, all confirmed working.**

### 7. Practical verdict

For ~6 small self-contained demo programs each needing Postgres and/or Redis: **yes, do it.** One `apphost.cs` plus six sibling `.cs` files, each with its own `#:package` directives, orchestrated with `AddCSharpApp`. `aspire run` brings up the containers, injects connection strings, and gives you a dashboard with per-resource logs and traces. Zero `.csproj` files in the repo.

The main caveats are experimental-API surface (`AddCSharpApp`), AOT-by-default, and Redis TLS — all listed below.

---

## Working examples

### A. Minimal file-based app with a package and shebang

*Verified working on SDK 10.0.302.*

```csharp
#!/usr/bin/env -S dotnet --
#:package Humanizer@2.14.1

using Humanizer;

Console.WriteLine(TimeSpan.FromMinutes(90).Humanize());
Console.WriteLine($"args: {string.Join(", ", args)}");
```

```bash
chmod +x app.cs
./app.cs one two
# or
dotnet run app.cs -- one two
```

### B. Multi-directive kitchen sink

*Every directive here was individually verified on SDK 10.0.302; the combined file is assembled by me, not quoted from docs.*

```csharp
#!/usr/bin/env -S dotnet --
#:sdk Microsoft.NET.Sdk.Web
#:package Npgsql@9.*
#:package StackExchange.Redis@2.*
#:property TargetFramework=net10.0
#:property LangVersion=preview
#:property PublishAot=false
#:include helpers.cs
#:project ../SharedLibrary/SharedLibrary.csproj

using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Helpers.Greet("world"));
app.Run();
```

`helpers.cs` (an included file — **no top-level statements allowed here**):

```csharp
public static class Helpers
{
    public static string Greet(string name) => $"Hi {name}";
}
```

### C. Single-file Aspire AppHost — Postgres + Redis + a file-based worker

**This exact file was built and run successfully.** The container images, the injected connection strings, and the worker's successful `select 1` are quoted in §6.

`apphost.cs`:

```csharp
#:package Aspire.Hosting.PostgreSQL@13.5.3
#:package Aspire.Hosting.Redis@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3

#pragma warning disable ASPIRECSHARPAPPS001

var builder = DistributedApplication.CreateBuilder(args);

var appdb = builder.AddPostgres("postgres").AddDatabase("appdb");
var cache = builder.AddRedis("cache");

builder.AddCSharpApp("worker", "worker/worker.cs")
       .WithReference(appdb).WaitFor(appdb)
       .WithReference(cache).WaitFor(cache);

builder.Build().Run();
```

`worker/worker.cs`:

```csharp
#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:package StackExchange.Redis@2.*

using Npgsql;

Console.WriteLine($"pg   = {Environment.GetEnvironmentVariable("ConnectionStrings__appdb")}");
Console.WriteLine($"redis= {Environment.GetEnvironmentVariable("ConnectionStrings__cache")}");

await using var ds = new NpgsqlDataSourceBuilder(
    Environment.GetEnvironmentVariable("ConnectionStrings__appdb")).Build();
await using var cmd = ds.CreateCommand("select 1");
Console.WriteLine($"pg query -> {await cmd.ExecuteScalarAsync()}");

await Task.Delay(Timeout.Infinite);
```

Run it:

```bash
aspire run
```

### D. Same AppHost, split into named databases and a separated `postgres` server resource

*Assembled by me from verified primitives — the `AddPostgres`/`AddDatabase`/`WithDataVolume` calls are quoted from the Aspire docs, but I have not run this exact multi-database variant.*

```csharp
#:package Aspire.Hosting.PostgreSQL@13.5.3
#:package Aspire.Hosting.Redis@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3

#pragma warning disable ASPIRECSHARPAPPS001

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
                      .WithDataVolume()
                      .WithPgAdmin();

var lockingDb = postgres.AddDatabase("lockingdb");
var outboxDb  = postgres.AddDatabase("outboxdb");
var cache     = builder.AddRedis("cache");

foreach (var demo in new[] { "01-row-lock", "02-advisory-lock", "03-skip-locked" })
{
    builder.AddCSharpApp(demo, $"demos/{demo}.cs")
           .WithReference(lockingDb).WaitFor(lockingDb)
           .WithReference(cache).WaitFor(cache);
}

builder.AddCSharpApp("outbox", "demos/04-outbox.cs")
       .WithReference(outboxDb).WaitFor(outboxDb);

builder.Build().Run();
```

### E. Adding integrations without hand-editing directives

```bash
aspire new aspire-empty --language csharp -n Demos -o .
aspire add postgres     # rewrites #:package Aspire.Hosting.PostgreSQL@13.5.3
aspire add redis        # rewrites #:package Aspire.Hosting.Redis@13.5.3
aspire run
```

*Verified: `aspire add` edits `apphost.cs` in place and reports e.g. `The package Aspire.Hosting.PostgreSQL::13.5.3 was added successfully.`*

---

## Sharp edges

1. **Native AOT is ON by default.** `PublishAot=true` is implicit (confirmed via `dotnet project convert` output). This breaks reflection-heavy packages and **disables Hot Reload under `dotnet watch`**. Add `#:property PublishAot=false` to most demo files.

2. **`PackAsTool=true` is also implicit.** Harmless, but surprising if you `dotnet pack`.

3. **`AddCSharpApp` is experimental.** Without `#pragma warning disable ASPIRECSHARPAPPS001` your AppHost will not compile. The API *"may change or be removed in future releases"*. Documented limits: **one `.cs` file per resource**, **local development only — no deployment support**.
   Source: <https://aspire.dev/integrations/dotnet/csharp-file-based-apps/>

4. **Aspire's Redis connection string now carries `ssl=true`.** Observed: `ConnectionStrings__cache=localhost:46019,password=…,ssl=true`. StackExchange.Redis will need to trust the Aspire dev cert. Aspire sets `SSL_CERT_DIR` for child resources (observed: `SSL_CERT_DIR=/tmp/aspire-dcp…/worker-…/certs:/usr/lib/ssl/certs:/home/fu/.aspnet/dev-certs/trust`), but a demo you run *outside* the AppHost will not have that.

5. **Aspire launches file-based resources with `--no-cache`.** Observed command line: `dotnet run --file …/worker.cs --no-cache …`. Every AppHost start rebuilds every demo from scratch — expect ~1s per demo of extra startup.

6. **`#:include` with a glob disables build caching.** Use explicit `#:include foo.cs` lines to keep runs at ~0.2s.

7. **CA2266 warns on every multi-file entry point without a shebang.** Just always start entry-point files with `#!/usr/bin/env -S dotnet --`.

8. **The two shebangs are not equivalent.** `#!/usr/bin/env dotnet` lets the dotnet CLI swallow `--help`, `-c`, `-f` etc. before your app sees them. Use `#!/usr/bin/env -S dotnet --`. LF endings, no BOM.

9. **Concurrent runs of the *same* file collide** over build outputs. Pre-build with `dotnet build file.cs`, then run instances with `--no-build`.

10. **Don't nest demo `.cs` files under a `.csproj` cone**, and watch out for inherited `Directory.Build.props` / `Directory.Packages.props` / `nuget.config` / `global.json` from parent directories — they silently apply.

11. **Cache invalidation is content-based, not path-based.** Docs warn: *"Changes to implicit build files don't trigger rebuilds"* and *"Moving files to different directories doesn't invalidate cache."* `dotnet clean file-based-apps` when things get weird.

12. **User secrets ID is a hash of the full file path** — moving a demo file loses its secrets.

13. **Stale DCP state will make `aspire run` fail confusingly.** On my first attempts I got `Polly.Timeout.TimeoutRejectedException … EnsureKubernetesAsync` / `The JSON-RPC connection with the remote party was lost`. This was **not** a single-file problem — the same AppHost ran fine after leftover `dcp` processes and `/tmp/aspire-dcp*` directories were cleared. If you hit it, kill stray `dcp` processes and remove `/tmp/aspire-dcp*`.

14. **CLI/package version skew.** My CLI was 13.3.5 against packages 13.5.3. It worked, but `aspire` nags to update. Keep them aligned (`aspire update`, and re-install the CLI).

15. **`aspire start` (background mode) was unreliable** here — `Timeout waiting for AppHost to start`, and `aspire ps` then reported no running AppHost. Use foreground `aspire run` for demos.

16. **`#:ref` is a trap.** It looks like the multi-file answer but is experimental and gated; use `#:include`.

17. **Omitting a package version fails** with `NU1015` unless you have central package management. Always write `@version` or `@*`.

---

## Sources

- File-based apps (primary reference): <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps>
- CA2266 analyzer rule: <https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2266>
- What's new in the SDK and tooling for .NET 10: <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/sdk>
- dotnet/sdk design doc for file-based programs: <https://github.com/dotnet/sdk/blob/main/documentation/general/dotnet-run-file.md>
- Announcing `dotnet run app.cs` (.NET Blog): <https://devblogs.microsoft.com/dotnet/announcing-dotnet-run-app/>
- Andrew Lock, exploring `dotnet run app.cs` (preview-era syntax): <https://andrewlock.net/exploring-dotnet-10-preview-features-1-exploring-the-dotnet-run-app.cs/>
- Andrew Lock, behind the scenes of `dotnet run app.cs`: <https://andrewlock.net/exploring-dotnet-10-preview-features-2-behind-the-scenes-of-dotnet-run-app.cs/>
- Multi-file support tracking issue: <https://github.com/dotnet/sdk/issues/48174>
- What's new in Aspire 13 (single-file AppHost GA): <https://aspire.dev/whats-new/aspire-13/>
- Aspire C# file-based apps in the AppHost (`AddCSharpApp`): <https://aspire.dev/integrations/dotnet/csharp-file-based-apps/>
- Aspire SDK (`Aspire.AppHost.Sdk`, `AspireUseCliBundle`): <https://aspire.dev/get-started/aspire-sdk/>
- Aspire AppHost concepts / structure: <https://aspire.dev/get-started/app-host/>
- Aspire PostgreSQL hosting integration: <https://aspire.dev/integrations/databases/postgres/postgres-host/>
- Aspire executable resources (`AddExecutable`): <https://aspire.dev/app-host/executable-resources/>
- `AddCSharpApp` API reference: <https://learn.microsoft.com/en-us/dotnet/api/aspire.hosting.projectresourcebuilderextensions.addcsharpapp>
- dotnet/aspire issue — support single file cs apphost: <https://github.com/dotnet/aspire/issues/9612>
- dotnet/aspire PR — enable `aspire add` for single-file AppHost: <https://github.com/dotnet/aspire/pull/11451>
- dotnet/aspire issue — add a C# file-based app as a resource: <https://github.com/dotnet/aspire/issues/11498>
- Announcing Aspire 9.5 (.NET Blog): <https://devblogs.microsoft.com/dotnet/announcing-dotnet-aspire-95/>
- NuGet flat-container version index (used to confirm 13.5.3): `https://api.nuget.org/v3-flatcontainer/aspire.hosting.apphost/index.json`

---

## Preview → GA changes (things that will be wrong in older blog posts)

| Thing | Preview form | GA form (verified) |
|---|---|---|
| `#:property` separator | space — `#:property LangVersion preview` | **`=`** — `#:property LangVersion=preview` |
| `#:sdk` version separator | space | **`@`** |
| Shebang | `#!/usr/bin/dotnet run` | **`#!/usr/bin/env -S dotnet --`** (fallback `#!/usr/bin/env dotnet`) |
| Multi-file | "not coming to .NET 10", deferred to .NET 11 | **`#:include` ships in SDK 10.0.300+** |
| `#:project` | added in preview 6 | supported |
| Native AOT | not default | **`PublishAot=true` by default** |
| Aspire single-file AppHost | Aspire 9.5 / RC1, behind `features.singlefileAppHostEnabled` | **GA in Aspire 13.0, no flag** |

Sources: <https://andrewlock.net/exploring-dotnet-10-preview-features-1-exploring-the-dotnet-run-app.cs/>, <https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/sdk>, <https://aspire.dev/whats-new/aspire-13/>

---

## Unverified / open

Things I could **not** confirm — do not write code against these without checking:

1. **`aspire init` creating a single-file AppHost.** I only have the <https://aspire.dev/whats-new/aspire-13/> claim that it *"create[s] single-file AppHost for quick starts"*. I did not run `aspire init` in an existing codebase, and the CLI help text does not spell out that behaviour.

2. **`dotnet new aspire-apphost-singlefile` output.** The template is listed in `dotnet new list aspire` on this machine, but I did not instantiate it. I instantiated `aspire new aspire-empty` instead, which produced a single-file AppHost — the `aspire-apphost-singlefile` output may differ slightly.

3. **The exact `AddCSharpApp` overload signatures.** The docs describe an `Action<ProjectResourceOptions>` overload with `LaunchProfileName` / `ExcludeLaunchProfile` / `ExcludeKestrelEndpoints`, but I could not fetch the Learn API page (it 404'd through my fetcher). I verified only the two-argument `AddCSharpApp(string name, string path)` form. **If you need the options overload, check IntelliSense before relying on it.**

4. **Whether `AddCSharpApp` handles a *multi-file* file-based app** (one whose entry point uses `#:include`). The docs say "single `.cs` file per resource"; I tested only a single-file worker. Ambiguous whether "single file" means "one resource per entry point" or "no `#:include` allowed". **Test before designing demos around it.**

5. **The `#:exclude` gating.** In one confounded test I saw `error: This is an experimental feature, set MSBuild property 'ExperimentalFileBasedProgramEnableTransitiveDirectives' to 'true' to enable it.` — but that was triggered by a transitively-included file carrying `#:ref`, not by `#:exclude` itself. A clean `#:include *.cs` + `#:exclude skip.cs` test **worked with no flag**. So `#:exclude` appears ungated, but I would not bet a demo on it.

6. **Build-cache location on Windows/macOS.** I observed `~/.local/share/dotnet/runfile/` on Linux, which contradicts the docs' `<temp>/dotnet/runfile/`. I did not test other platforms.

7. **Redis TLS specifics.** I confirmed `ssl=true` appears in the injected connection string but did **not** verify a StackExchange.Redis round-trip against it (only Postgres/Npgsql was round-tripped). **Expect to fight the dev cert here.**

8. **`aspire deploy` / `aspire publish` with a single-file AppHost.** <https://aspire.dev/whats-new/aspire-13/> claims they work; the `AddCSharpApp` docs simultaneously say file-based app *resources* are *"local development only (no deployment support)"*. These two claims are in tension. Untested — irrelevant for demos, relevant if you ever want to ship one.
