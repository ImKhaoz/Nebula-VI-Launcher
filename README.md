<p align="center">
  <img src="Assets/NexusLogo.png" alt="Nebula logo" width="132">
</p>

<h1 align="center">Nebula · GTA VI Edition</h1>

<p align="center">
  A fullscreen Windows launcher for a local GTA VI package and community VIctory builds.
</p>

<p align="center">
  <a href="#getting-started">Getting started</a> ·
  <a href="#runtime-builds">Runtime builds</a> ·
  <a href="#building-from-source">Building from source</a> ·
  <a href="#independence-and-distribution">Independence</a>
</p>

---

Nebula keeps game setup, runtime selection, and shader preparation in one place. Choose your local package, install a VIctory build, and switch between installed builds from the same launcher.

> [!IMPORTANT]
> VIctory compatibility is experimental. Startup and gameplay depend on the title package, GPU, driver, and runtime build. Nebula provides the launcher and does not supply the game.

## Getting started

1. Extract the Windows x64 release and open **Nebula-GTAVI-Version.exe**. The release ZIP is self-contained and includes the .NET 8 Windows desktop runtime, so you do not need to download or install .NET separately. Windows 11 normally includes Microsoft Edge WebView2; if the dashboard cannot open, install the [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section).
2. In **Settings → VI Setup**, choose the root folder of the local GTA VI Xbox Series package you are authorized to use. Nebula detects the executable in that folder.
3. Under **VIctory builds**, use **Install Git** and **Install Rust** if needed. Install [Visual Studio Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/) with **Desktop development with C++** and a Windows SDK.
4. Select **Install** beside a runtime. Nebula fetches its pinned source revision from GitHub and builds it locally. When it finishes, the build appears in the **Compatibility runtime** selector.
5. Select an installed runtime and choose **Launch**. When pre-warming is needed, the launcher shows progress and saves the translated shaders for later launches with that build. Choose **Skip pre-warming** on that screen to start the game immediately; uncached shaders may compile during play.

In **Settings → Preferences**, **Pre-warm shaders before launch** controls pre-launch cache preparation for XCL, built-in runtimes, and compatible custom VIctory repositories. **Developer Mode** shows a live console on the library and launch screen; it forwards shader progress and runtime logs, including FPS and draws/frame when the selected runtime reports them. Use **Save log** or **Clear** in the console controls.

On first launch, Nebula presents its legal disclaimer before continuing. Accepting it is saved on this PC; choosing **Exit** closes the launcher. At startup, the launcher checks the configured GitHub repository's latest release and shows a banner when a newer version is available. The banner opens the release page; updates are downloaded and installed manually.

## Runtime builds

| Build | Pinned revision |
| --- | --- |
| **VIctory World** | [ZSlayerHQ · `99f8834`](https://github.com/ZSlayerHQ/zslayer-pc-port-runtime/commit/99f8834aa16b98d7f3943b8de262a8d4857bda7b) |
| **Nvidia 8g** | [ad3lre · `03ebc87`](https://github.com/ad3lre/victory-nvidia-8gb/commit/03ebc87da39d6fd843ddc74139c755661fcc7ebf) |
| **VIctory NVIDIA** | [trustno1-coder · `3e5fb36`](https://github.com/trustno1-coder/VIctory-NVIDIA/commit/3e5fb36392796602fce70aa7331f4100be9291af) |
| **VIctory AMD** | [myokex · `070df42`](https://github.com/myokex/victory-amd/commit/070df424b0967dca422478a1f12afaaf6736ebdd) |

Runtime builds can take several minutes. Nebula stores settings, installed runtimes, and caches in `%LOCALAPPDATA%\Nebula-GTAVI`. Launch logs are under `VIctory\launcher-logs` in that directory. The Nvidia 8g runtime is built from [ad3lre's fork](https://github.com/ad3lre/victory-nvidia-8gb), pinned to the commit above; **Install Latest** follows its `main` branch.

Each runtime has two actions under **Settings → VI Setup**. **Install** builds the pinned revision listed above. **Install Latest** fetches that runtime's configured upstream branch and builds it in a temporary folder; Nebula replaces the installed executable only after the build succeeds. If upstream changes code touched by Nebula's shader-progress patch, the latest build may fail to apply, leaving the current installed executable available.

For **VIctory NVIDIA**, the fork recommends the Series S profile. Set it under **Settings → VI Setup → Console profile**. Nebula enables the fork's Series S interface compositor and uses its `shaders` command to prepare a separate cache.

### Custom VIctory repositories

Under **Settings → VI Setup → Custom runtime repository**, enter a public GitHub repository URL and, optionally, a branch. Leave the branch blank to fetch the repository's default branch. Nebula fetches the source, builds it with Rust in a temporary work folder, and lists the installed runtime in **Compatibility runtime** after it produces `target/release/victory.exe`. Submitting the same repository and branch again builds and updates that entry. Custom builds use their own shader cache directory. **Open runtime folder** opens the selected installed runtime's location.

For the [`Op-lite/victory-scarlett`](https://github.com/Op-lite/victory-scarlett) `scarlett-fox` branch, add that repository as a custom runtime. When selected, Nebula follows that fork's cache workflow: it translates `shaders.rpf` into the runtime's persistent cache before launch, and on later launches warms pipelines recorded by earlier gameplay. The same cache directory is passed to both prewarming and gameplay, and the fork records newly used pipelines during a game session for the next launch. **Game resolution** sets the fork's reported display mode and maximum window size; the default is 1920 × 1080. **Ray-traced lighting** is off by default and can be enabled for a launch; Nebula explicitly passes the saved choice so inherited environment variables cannot override it. With ray tracing off, Nebula keeps the fork's lightweight ambient fill enabled. This reduces shadow accuracy while avoiding its more expensive BVH builder. The fork's run script describes ray tracing as about one-third slower, based on its tested setup; this is not a measured FPS guarantee for other GPUs. This preparation only covers shaders and pipelines the fork can translate and build; it cannot supply missing game assets or make unsupported rendering features work.

The installed Scarlett-Fox runtime manifest identifies commit [`7c8f019`](https://github.com/Op-lite/victory-scarlett/commit/7c8f019ae6196735049d2ba300a7bfb0f4c8a4f2), the latest `scarlett-fox` commit checked for this build. Nebula applies the fork's measured run-118 steady-state switches plus run-128 staging and memory trims. It deliberately leaves out frame-log diagnostics and the small-buffer VRAM placement option for this 6 GB GPU. The fork reports its tests on an RTX 4080 SUPER with 32 GB RAM, with frame-time variation between runs, so the profile is a reasoned starting point rather than a performance guarantee for an RTX 3050.

The repository must provide a root Cargo workspace with a package named `victory` and a CLI compatible with Nebula's `run <game.exe> --console <profile> --execute` launch arguments. Shader pre-warming uses the standard `warm` command when supported; forks that do not support it can still launch with pre-warming skipped. Nebula disables the Nvidia 8g fork's optional anonymous telemetry for launcher-started runs with `--no-telemetry`. Nebula cannot make unrelated or incompatible repositories run as VIctory runtimes.

Only install repositories you trust. Building Rust projects runs their build scripts and dependencies on your PC. Nebula accepts public HTTPS GitHub repository URLs; private repositories and GitHub subfolder links are not supported.

Before compiling a downloaded runtime repository, Nebula asks Microsoft Defender to scan the staged source with remediation disabled and checks the scan's threat records. A detected item, missing scanner, failed scan, or unverifiable result pauses installation for review; choose **Continue anyway** only if you trust the repository, or cancel to stop the build.

## Building from source

Use Windows 11 with the .NET 8 SDK and PowerShell 7. The launcher builds independently of the optional runtime repositories.

```powershell
dotnet run --project .\Nebula-GTAVI.csproj
```

To produce a self-contained Windows x64 ZIP and SHA-256 checksum, run the release script from the repository root. Its output goes to `release/`. If the GitHub repository is not yet created, omit `-Repository`; the package still runs self-contained, and update checking stays disabled until a repository is set.

```powershell
.\tools\Publish-NebulaRelease.ps1 -Version 0.1.0-preview.19 -Repository OWNER/REPOSITORY
```

After creating the GitHub repository, set it on the release build with `-Repository OWNER/REPOSITORY`. Increment the semantic version for every release and publish the generated ZIP as an asset. Nebula checks the GitHub latest-release endpoint at startup when a repository is configured and does not silently download or replace itself.

## Independence and distribution

Nebula is an independent community project. It is not affiliated with, endorsed by, or sponsored by Rockstar Games or Take-Two Interactive. Grand Theft Auto and related marks belong to their respective owners.

This repository distributes launcher source, Nebula interface assets, and VIctory integration patches. The release ZIP contains the launcher and its desktop dependencies. Neither contains GTA VI game files, executable builds, keys, saves, shader caches, or compatibility runtime binaries. Nebula does not download a title package.

VIctory integration patches are supplied with the [upstream GPL-3.0 license text](licenses/VIctory-GPL-3.0.txt). The bundled HLS.js component has its [own license notice](Dashboard/vendor/hls.js-LICENSE.txt).
