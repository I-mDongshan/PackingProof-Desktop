# 发布与运行时开发规范

修改发布、启动器、更新、FFmpeg、LibVLC 或 TTS 前必须阅读本文件。通用开发、测试、提交和安全规则仍以仓库根目录 `AGENTS.md` 为准。

## 运行时与发布包布局

- 发布脚本默认只生成目录包和 Setup；完整 7z 与完整 ZIP 仅在分别传入 `-IncludeSevenZip`、`-IncludeFullZip` 时作为本地产物生成，都不上传到 Release。清爽包根目录主要包含 `ExpressPackingMonitoring.exe` 与 `app\`；应用负载、依赖、Web 文件、LibVLC 和 `tools\ffmpeg.exe` 位于 `app\`。
- 运行时数据统一存放在 `%LOCALAPPDATA%\ExpressPackingMonitoring\`。发布包不得包含 `config.json`、`videos.db`、缓存、日志、录像或其他本机状态。
- 共享运行时布局（安装目录下的 `runtime\`）用来让桌面端与 QQ 机器人共用同一份 .NET 运行时：`runtime\dotnet.exe` + `runtime\host\fxr\<版本>\hostfxr.dll` + `runtime\shared\Microsoft.NETCore.App|<WindowsDesktop.App>\<版本>\`，外加记录框架版本、逐文件 SHA256 与老布局迁移映射的 `runtime\layout.json`。自包含应用里的运行时是私有的，别的程序借不到；只有共享框架布局能被多个程序复用。用 `Tools\Build-SharedRuntimeLayout.ps1` 生成并自检，`Tools\Test-SharedRuntimeLayout.ps1` 可单独复核。
- 应用本身是**自包含发布**（`app\` 里有 `coreclr.dll` / `hostfxr.dll`，运行时平铺在 `app\` 里），包根**不再**放 `runtime\`。这是为了让**任何启动器都能装上增量包**：老启动器只认 `app\` 内的条目、也不认 `root` 落点（见下"升级时序"），而 0.0.82 现场已经证明"先换启动器再装运行时补丁"这条路在老装机上走不通。共享布局由**主程序首次启动时自己落成**：出包时按布局清单生成 `app\runtime-seed\`（布局里自包含产物没有的那几个小文件），主程序启动早期调用 `ExpressPackingMonitoring.UpdateCore\RuntimeLayoutSeeder.cs`，给 `app\` 里已有的运行时文件在安装根目录 `runtime\` 下建**硬链接**（同一份磁盘数据两个名字，不额外占盘；不支持硬链接时退化成复制），逐文件核对 SHA256、整份布局自检通过后才整体改名落地；只在根目录还没有 `runtime\` 时动作，失败就放弃（扩展退回系统运行时）。验证入口：`pwsh -NoProfile -File Tools\Test-RuntimeLayoutSeed.ps1`（自包含发布 → 种子 → 落成 → 框架依赖的探针带 `DOTNET_ROOT` 真的从布局加载运行时）。打包脚本会在出包时强校验：自包含特征文件必须在、每个布局文件必须有同名同哈希来源或进种子、种子内容与清单逐文件一致。
- **升级时序与能力协议号**：版本号说明发的哪一版，能力号（`ExpressPackingMonitoring.UpdateCore\LauncherProtocol.cs`）说明**能做到什么**：`patch_package.required_launcher_protocol` 表示"这份补丁至少要能力 N 的启动器才能装"（带 `root=runtime` 载荷的过渡补丁必须 ≥2），`launcher_package.required_app_protocol` 表示"这个启动器至少要能力 N 的主程序才能换"。四个检查点缺一不可：① 主程序准备补丁前发现本机启动器能力不够 → **不下补丁**，先更新启动器（约 3MB）并提示重启（`AppPatchPreparationStatus.LauncherUpdateRequired`）；② 启动器后台下载前不满足 → 不下载；③ 启动器应用 pending 前不满足 → 把这份补丁挪到 `pending-rejected\<版本>` 并明确提示（**不再每次启动失败一遍**，0.0.82 现场就是这样循环的）；④ 主程序替换启动器前发现启动器包要求更高的应用能力 → 不替换。已安装启动器的能力由它自己每次启动写进 `launcher-protocol.json`；文件缺失（0.0.81 及更早从来不写）、内容不合法、或记录的哈希与当前启动器文件不符（刚被安装器换过）都按**最低能力**处理。另外，启动器自更新的"成功检查"缓存必须有复查窗口（`SuccessfulCheckWindow`，当前 6 小时）：0.0.82 现场就是"应用版本没变 + 启动器文件没动"于是**永久跳过**，老机器永远拿不到新启动器，需要新启动器的补丁也就永远装不上。
- **过渡补丁（`root=runtime` 载荷）已退役**：它当初的设想是"主程序先把根启动器换掉（`launcher_package`），新启动器在下次启动时应用带运行时的补丁"。**这条路在老装机上不成立**——启动器自更新只在**应用版本变化后**才重新跑，而应用版本变化又得靠一次能装上的补丁，成了循环；0.0.82 现场因此出现"补丁下好了、老启动器在 `ValidatePatchFiles` 阶段拒绝（`Patch 文件不存在：dotnet.exe`，安全中止、没动文件）、而新启动器永远装不上"的死循环。所以自 0.0.83 起改为**自包含发布 + 主程序自己落成共享布局**（见上），老启动器能直接装新应用的补丁。`-IncludeRuntimeInPatch`、`root` 字段与 `files\.runtime\` 布局、`Tools\Test-TransitionPatch.ps1` 都保留可用（协议号机制保证它不会被发给他装不了的机器），但正常发布不再使用；带它出包时必须声明 `required_launcher_protocol` ≥ 2。
- **运行时分界线与补丁基线抬升**：换 .NET 大版本的那一版（当前是 **0.0.83**，自包含 .NET 10）是"运行时分界线"，它自己**必须保持 `-PatchBaselineVersion 0.0.18`**，否则 0.0.18~0.0.82 的机器会被判成"低于补丁基线"而拿不到这次升级、白白退回完整包。抬升放在**下一版**（0.0.84 用 `-PatchBaselineVersion 0.0.83`）：从此 0.0.83 及以上走普通增量包，低于 0.0.83 的机器不再收到增量包，启动器按 `VersionBelowBaseline` 提示下载完整安装包——这就是"所有用户必须先经过 0.0.83"的落地方式。
- 当前应用目标框架与随包运行时基线都是 **.NET 10**（本机实测 10.0.13）。宿主不会用 10 的运行时去跑请求 8.x 的应用（.NET 7 起没有多级回退），所以桌面端与市场里的 .NET 扩展（QQ 机器人）必须**同大版本**升级；升级后布局里的框架目录名会跟着变（`shared\Microsoft.WindowsDesktop.App\10.0.x`）。
- 工具链门槛：`C:\Program Files\dotnet\sdk\<版本>\minimumMSBuildVersion` 决定 IDE 能不能解析这个 SDK，.NET 10 写的是 `18.0.0`（.NET 9 是 `17.12.0`）。所以本机 IDE 必须是 **Visual Studio 2026（18.x）**；VS 2022（MSBuild 17.14）会报"未能正确加载解决方案中的一个或多个项目"，实际是 `MSB4236`/`error : 无法解析 SDK"Microsoft.NET.Sdk"`。命令行 `dotnet build`、`dotnet test` 和发布脚本走 SDK 自带的 MSBuild，不受影响，所以出现这个对话框先看 VS 版本，不要去改 `.sln`。
- 市场里的框架依赖扩展（QQ 机器人）不能用在没有 `runtime\` 的桌面端上：那种机器只能靠系统 .NET，而老桌面端是自包含的、不提供新大版本运行时。所以插件发布时要把 manifest 的 `minPackingProofVersion` 抬到**第一个带 `runtime\` 的桌面端版本**（当前是 `0.0.63`），市场才不会把它推给老桌面端。
- 打包后按这三条核产物：`app\` **有** `coreclr.dll`/`hostfxr.dll`（证明是自包含发布）、`app\runtime-seed\layout.json` 在且种子逐文件与布局清单一致、包根**没有** `runtime\`（共享布局由主程序首次启动落成）；`pwsh -NoProfile -File Tools\Test-RuntimeLayoutSeed.ps1` 会一次跑完这几条并验证子进程确实从落成的布局加载运行时。安装器冒烟：`Setup /SILENT /SUPPRESSMSGBOXES /NORESTART /DIR=<临时目录>` 装完确认 `app\coreclr.dll` 在、包根没有 `runtime\`，再用 `unins000.exe /SILENT /SUPPRESSMSGBOXES` 卸载，确认安装目录连同 `app\` 一起清空、且 `%LOCALAPPDATA%\ExpressPackingMonitoring` 未被动过（静默卸载不带 `/EPMUNINSTALLOPTIONS` 不会删设置与录像）。
- `ffmpeg.exe` 可从 `app\tools\ffmpeg.exe`、应用运行目录或系统 `PATH` 解析。AppPatch 不携带 FFmpeg，因此应用逻辑必须兼容用户机器长期保留的旧版本。
- LibVLC 收录播放所需的解码、解封装、字幕、滤镜和输出插件，只排除 `access_output`、`mux`、`services_discovery`、`stream_out`、`visualization`、`lua`；规则集中在 `ExpressPackingMonitoring.csproj`。发布时移除设计时程序集。
- Edge TTS 是默认在线语音；Kokoro 模型和运行时默认不随包发布。完整包包含生成的默认 Edge TTS 缓存，AppPatch 必须排除 TTS 缓存。

## FFmpeg 兼容基线

- 正式基线固定为 FFmpeg 4.4.1 Essentials，以兼容 Win7 老显卡的 NVENC API 11.1。AV1 不作为产品能力，选择 AV1 时回退 H.265；暂不实施 8.0.1 + 4.4.1 双基线。
- 高级用户可在 Win8+ 自行替换 `app\tools\ffmpeg.exe`，官方不保证该自定义环境。
- 禁止假设 CLI 参数跨版本通用：FFmpeg 8.x 已移除 RTSP `-stimeout`；4.4.x 的 RTSP `-timeout` 会挂起，因此网络摄像头不传 socket 超时参数，由应用层 15 秒连接超时和断流看门狗兜底。`-fps_mode` 仅 5.1+ 可用，旧版回退 `-vsync passthrough`。参数策略集中在 `NetworkCameraSource.BuildArguments`。
- 网页转码的编码器不能写死：`h264_nvenc` 只在 NVIDIA 机器上存在，AMD/Intel 显卡和 macOS 每次播放都要先失败一次再回退 CPU，Mac 上更是永远拿不到硬件编码。平台候选与参数集中在 `WebTranscodeEncoderPolicy`，用 `ffmpeg -encoders` 探测一次并缓存，探不到就直接走 `libx264`；硬件参数按编码器分别给（NVENC `-preset p1 -cq 30`、QSV `-global_quality`、AMF `-rc cqp`、VideoToolbox `-q:v 45 -realtime true`），不能跨编码器照搬。
- 修改任何 FFmpeg 调用前，必须使用 `Tools/ffmpeg-baseline.json` 锁定的 4.4.1 和至少一个其他受支持主版本（如 8.0.1）验证受影响流程；同步更新 `NetworkCameraSourceTests` 参数断言和随包 FFmpeg 参数识别测试。
- macOS 不自带 FFmpeg（`avconvert`、`qlmanage`、`sips` 都不是 ffmpeg CLI 的替代品），因此 Mac 主机必须随包携带二进制：基线锁在 `Tools/ffmpeg-macos-baseline.json`，用 `Tools/Prepare-PinnedFFmpegMac.sh` 下载校验到 `package/dependency-cache/ffmpeg-macos/<版本>/`，`Tools/Publish-MacHost.sh` 再拷进 `Contents/MacOS/Host/tools/ffmpeg`。缺这个文件时缩略图、预览和网页转码会全部失败，网页会显示"编码未知"并回退直接播放原片。
- macOS 基线固定为 Martin Riedl Build Server 的 arm64 9.0.2 静态 GPL 构建：原生 arm64、只依赖系统框架、上游已用 Developer ID 与加固运行时签名，且未启用 `--enable-nonfree`（可随包分发）。版本 ≥5.1，因此 Mac 主机始终走 `-fps_mode passthrough` 分支。

## 启动器与增量更新

- 用户应从包根目录启动器进入。启动器立即启动主程序、后台检查并下载经过校验的 AppPatch 到 `cache\updates`，下次启动时安装。
- 增量包下载带断点续传：主程序与启动器共用 `ExpressPackingMonitoring.UpdateCore\ResumableFileDownload.cs`，半截包按版本留在 `cache\updates\partial\*.partial` 里，断线、停滞、换源（GitHub ↔ Gitee）、跨重启都带 `Range` 从断点继续。服务端不认 `Range`（回 200/416）就截断重下，绝不拼两段不同来源的字节；最终仍要过体积 + SHA256 双校验才发布到 `pending`，校验不过的断点会被清掉并只重下一次。下载停滞判据是"60 秒没有新字节"而不是绝对时长：主程序端增量包不再用清单那 75 秒的 `HttpClient` 总超时（否则 75MB 的过渡包在慢网上正好卡在下到一半），启动器的下载也单独给 30 分钟预算；超时保留断点，重试从断点继续。
- 启动器在启动主程序前处理共享运行时布局：安装目录里有可用的 `runtime\` 就把子进程的 `DOTNET_ROOT` 指向它（框架依赖的主程序因此用安装自带的运行时，不依赖机器另外装的 .NET）；没有或不完整就保持原样，走系统运行时，兼容 Gitee 的 no-runtime 安装包。老装机升级上来时 `app\` 里仍留着平铺运行时副本，其中 `app\hostfxr.dll` 会改变宿主行为，启动器按 `runtime\layout.json` 的 `appFileMap` 把它们搬进 `runtime\.legacy\` 留作回滚（只搬布局里有、且不在应用自身 deps.json 里的文件），连续两次正常启动后清理备份；主程序正在运行时只解析布局、不动任何文件。另外，过渡补丁如果被**老启动器**应用，整套运行时会落在 `app\runtime\`（老版本把所有条目都按 `app\` 解析），框架依赖的主程序找不到运行时；启动器在根目录还没有 `runtime\`、且 `app\runtime\` 里确实是一份完整布局时把它搬回根目录，用户重启一次即可自愈。验证入口：`pwsh -NoProfile -File Tools\Test-SharedRuntimeLaunch.ps1`（含"运行时装在 `app\runtime\`"这一场景）。
- 主程序可通过独立校验的 `launcher_package` 更新根启动器：等待旧进程退出、持有共享更新互斥体、只替换标准根启动器，失败时恢复旧文件。
- AppPatch 是固定基线累计补丁，当前默认基线为 `0.0.18`；启动器基线与 AppPatch 基线相互独立。
- 增量清单的文件条目支持可选 `root` 字段（缺省 `app\`，另一个允许值是 `runtime`），启动器按它决定落点，去重键是「位置 + 相对路径」，因此同一相对路径可以分别出现在两个位置；`Tools\Apply-AppPatch.ps1` 用同一套语义，备份也按位置分开。只有过渡补丁会写这个字段，普通增量包不带。
- AppPatch 默认只新增或覆盖清单中的文件；要从安装目录移除文件必须走清单里的**清理列表**（自 0.0.83 起实现）：出包时按"基线目录里有、当前发布目录里没有"算出 `deletes`（写成 `{"path": "..."}`，可带 `root`），安装端**先备份再删、失败整体回滚**，删完收掉被清空的目录，并拒绝同一路径既写入又清理。白名单不删：`tts_cache\`（缓存）与用户可替换的 `ffmpeg.exe` / `libvlc\`；只允许删 `app\`（或 `runtime\`）内的文件。**边界**：只有认得 `deletes` 的启动器（0.0.83 重建基线起）会执行清理，老启动器与手动安装脚本都只处理 `files`；而且清理列表是"基线 − 当前"算出来的，**基线一旦抬升，上一代漏清的残留下一次就不会再出现**，届时只能靠主程序做一次性退役清理。功能迁移不得把安装目录残留文件视为用户仍在使用该功能。
- 启动器基线由 `Tools/launcher-baseline.json` 锁定。普通应用发布复用锁定字节，不重建或重复上传 LauncherPatch。启动器逻辑输入变化时运行 `Tools/Publish-LauncherBaseline.ps1`、提交新锁并创建普通 `launcher-vX.Y.Z` 标签，不为该组件标签创建 Release。
- 启动器的 AOT 构建产物不可复现（同一份源码、同一台机器多次构建的哈希都不相同），所以 `Tools/launcher-baseline.json` 里的哈希必须与那一次构建产出的 `PackingProof_LauncherPatch_v*.zip` 成对保留：基线脚本产出的补丁包要就地留着，打包时用 `-LauncherBaselinePackagePath` 指给它，不要重新构建一份来对齐锁。重建基线会改变锁与补丁包哈希，需要重新提交锁并移动组件标签。
- AppPatch 与 LauncherPatch 是两个独立 ZIP，各自带手动安装器和说明；AppPatch 绝不能包含启动器。
- 更新地址通过环境变量或 `.env` 配置；默认检查 GitHub latest release API。

## 打包与发布流程

- macOS 包（`Tools/Publish-MacHost.sh`）产出 `.app` 与 `PackingProof-macOS-<版本>.dmg`；设置 `SIGN_IDENTITY` 时走 Developer ID + 加固运行时并在 DMG 与 .app 上装订公证票据。macOS 不做增量补丁：逐文件替换会破坏签名与公证，升级一律整包替换（下载 DMG、拖进应用程序）。Mac 端只通过 `UpdateCheckService` 检查并提示新版本，不在应用内替换自身。

- macOS 包只能在 Mac 上构建（swift + codesign），Windows 侧发布脚本够不到，所以用 `Tools/Publish-MacRelease.sh [版本号] [github|gitee|both]`：校验工作区干净且标签指向当前提交 → 构建并签名/公证 DMG → 挂到**同一个 `vX.Y.Z` Release**（不另开 mac 标签）→ 在 `update_v<版本>.json` 里补 `platforms.macos`（只作下载入口与人工核对）。版本号两个平台共用；**更新检查按"这个 Release 里有没有本平台的发布资产"挑版本**：macOS 只看 DMG，Windows 只看 `update_v<版本>.json`，所以只修 Windows 的版本 Mac 端会自动跳过，只发 macOS 的版本 Windows 端也会自动跳过（应用内检查与启动器自动检查都按这条挑，`UpdateReleaseSelection` 是唯一判定处）。单独发 macOS 时必须打对应的 `vX.Y.Z` 标签；只发 Windows 时无需任何额外操作。
- **预览版（prerelease）默认不推送给客户端**：标了预览版的版本会被正式机器整条跳过，只认最高的正式版；只有测试机才会照常收到它：在 设置 → 高级设置 → 接收预览版更新 里打开即可（应用内检查与启动器读同一份应用配置），也可以设环境变量 `UPDATE_ALLOW_PRERELEASE`（1/true/yes/on，优先级更高，便于临时覆盖）。草稿（draft）任何情况下都不推送。所以"先标预览版发给内部测、测完去掉预览标记"是安全节奏：正式机器在去掉标记前不会升上去。渠道开关与判定在 `UpdateChannelPolicy` / `UpdateReleaseSelection`，应用内检查、启动器自动更新、启动器包更新三处共用同一份逻辑。

- 发布版本维护在仓根 `Directory.Build.props` 的 `<Version>`（所有工程继承同一份，工程文件不得自己再写 `<Version>`；`ProjectVersionSourceTests` 守这条），并与 `vX.Y.Z` 标签一致。之所以收敛成一处：应用显示版本、更新判断读的是 `Config/AppVersion.cs`，而它跟 `Logging/RuntimeLog.cs` 一起被编进 **Core** 程序集，Core 与主程序各写一份版本时开发构建会读到旧号（0.0.79 之后界面一直显示 0.0.78，Mac 打包也拿 Core 的版本号），正式包因为发布脚本全局覆盖 `InformationalVersion` 才看不出来。对应版本标签位于 `HEAD` 且工作区干净时，正式产物和 `InformationalVersion` 只使用纯版本号；未打对应标签的测试包使用 Git 标准的 `-<距最近标签提交数>-g<短CommitID>` 后缀，脏工作区再追加 `-dirty`。AppPatch、更新清单和包内协议版本始终使用纯语义版本，完整 Commit ID 继续写入程序集元数据。基线、完整包和 AppPatch 必须复用同一次发布生成的主程序文件，保证测试包身份可追溯且不影响更新比较。
- 代码改动一律走远程 PR，不再直接向 `main` 推送提交。提到哪个平台按问题来源决定：我们自己发现的 **bug** 先在 GitHub 开 issue，再提 PR 并在说明里关联那个 issue；性能、功能、工具、文档这类**不是 bug** 的改动直接提 PR，不必为了留痕再补一个 issue。别人在某个平台提的 issue，PR 就提到那个平台（Gitee 的 issue 提 Gitee PR，GitHub 的 issue 提 GitHub PR）。PR 默认两边都提；目标可以用仓库根目录 `.env` 的 `PR_TARGET_HOST`（`gitee` / `github` / `both`，默认 `both`）或命令行 `-Target` 覆盖。用 `pwsh -NoProfile -File Tools\Submit-ChangePr.ps1 -Title "<PR 标题>" [-Merge] -Confirmed` 推送分支、创建 PR，用快进合并（提交原样保留）、把主干对齐到同一个提交；**不加 `-Confirmed` 只打印计划并拒绝执行**，提交 PR 前必须先经人工确认。
- 发布顺序固定为：在功能分支提交并保持工作区干净 → 运行本地 CI → 提 PR 并合并到主干（快进合并，提交原样保留，不改写 SHA，不 squash）→ 同步主干 → 在合并后的提交上创建本地 `vX.Y.Z` 标签 → 先按 `docs/development/RELEASE_CHECKLIST.md` 做变更审计（竞态/性能/关键路径），再以该标签身份执行一次 Release 构建、全量测试、自动验收、打包和产物校验 → 只推送该标签到 GitHub/Gitee → 创建 Release 并上传已校验产物 → **生成并上传 Gitee 专属的 no-runtime 安装包**（`pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag vX.Y.Z -UploadGitee`，见下方资产表；macOS 包由 Mac 侧 `Tools/Publish-MacRelease.sh` 挂到同一个 Release）。标签必须指向已在主干上的提交且先于正式构建：既避免先构建测试身份再为正式标签重复编译，也避免 PR rebase 之后标签悬空。
- 打包脚本会在产物目录生成 `release_commits_v<X.Y.Z>.txt`（上一个正式版以来的全部提交，仅本地核对、不上传），并按需生成或保留 `RELEASE_NOTES_v<X.Y.Z>.md`；重新打包不会再冲掉已经写好的发布笔记。
- 发布脚本 `Tools/Publish-Releases.ps1` 属于门禁的一部分：它校验发布笔记的分段与占位符、校验 `update_v<X.Y.Z>.json` 的 `title` 与 `notes` 是否已填写，并在打印提交清单后要求显式传入 `-ConfirmCommitCoverage`。只想自检用 `-ValidateOnly`，已经发布过的版本要补正文用 `-UpdateNotes`（只更新正文与标题，不重复上传附件）。
- 本地 CI 命令为 `pwsh -NoProfile -File Tools/Test-CI.ps1`，它与 `.github/workflows/ci.yml` 保持同一还原、构建、单元测试和 JavaScript 语法检查门禁。发布前必须先通过本地 CI，再运行 `Tools/Test-Release-Automated.ps1`；任一失败都不得推送标签或发布。
- 开始构建前先跑 `pwsh -NoProfile -File Tools/Check-ReleasePrereqs.ps1` 自检本机发布条件（工作区、标签与版本一致性、启动器基线与逻辑输入指纹、gh/gitee 登录态、dotnet、7-Zip、Inno Setup）。只读检查，不构建、不上传、不打印凭据。启动器逻辑输入变化时它会直接要求先重建基线，避免打包到一半才返工。
- `.github/workflows/release-package.yml` 只响应 `v*.*.*` 标签或手动触发，不再响应普通 `main` push。GitHub 侧仅对已在本地通过门禁的标签执行一次发布包构建，避免每次提交都耗电打包。
- 推荐运行 `打包脚本-增量.bat v<X.Y.Z>`。直接调用时使用：

```powershell
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1 -Version <X.Y.Z> -PatchBaselineVersion 0.0.18 -BaselineAppDir "package\ExpressPackingMonitoring+v0.0.18\ExpressPackingMonitoring+v0.0.18\app"
```

- `-BaselineAppDir` 必须指向真实固定基线的 `app` 子目录并包含 `tools\ffmpeg.exe`。脚本从目录解析实际基线，并强制与更新清单和补丁清单一致，禁止手工伪造。
- `-ReuseExistingLauncherBaseline` 只用于同一发布标签重发；普通新版本不传。
- 正式标签构建通过后，只把 `vX.Y.Z` 标签推送到 GitHub 与组织 Gitee 仓库 `PackingProof/PackingProof-Desktop`（`main` 已在 PR 合并时更新，不再直接推送提交），然后创建 Release。禁止普通 `main` push 触发发布包工作流。
- 发布前执行 `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`。不得在未完成真实设备检查时传 `-ConfirmManualCoreChecks`。
- 采集链路的现场结论（走哪条后端、GPU 转换有没有真生效、每帧整帧拷贝要花多少）用 `pwsh -NoProfile -File Tools/Diagnose-CameraPipeline.ps1` 一次跑出来：报告与探针日志写到输出目录，只读配置、录像和现场 `runtime.log`。整帧拷贝那一段不需要摄像头、也不用退出主程序，随手可跑；GPU/CPU 对照要独占摄像头，脚本会提示先关程序。
- `docs/development/RELEASE_CHECKLIST.md` 是发布前 AI/自动化照着跑的清单（命令与产物校验）；需要人和设备才能做的现场检查在 `docs/development/RELEASE_FIELD_CHECKS.md`，发布时提醒执行但不阻断，也不用写进发布说明。
- **打包前先审计**上一版本以来的完整变更，追踪录像、更新、授权、备份、删除和文件替换等关键路径；自动测试通过不能替代这一步。可信的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非用户明确接受记录在案的例外。

## 发布笔记与资产

- 发布笔记必须使用 `docs/development/RELEASE_NOTES_TEMPLATE.md`，并**逐条**核对 `release_commits_v<X.Y.Z>.txt` 里的全部提交（等价于 `git log --oneline <上一正式版标签>..HEAD`）。按“功能与体验 / 问题修复 / 兼容与工程”填写，覆盖所有用户可见变化；纯工程或测试提交也要在《兼容与工程》里落到文字，不能只写几条最重要的就交付。《问题修复》只写**上一个正式版就存在**的缺陷；本版自己引入又在本版修掉的回归不算问题修复（对用户来说那只是新功能还没定稿），把最终行为并进对应功能的《功能与体验》，纯内部返工不写。
- 发布笔记写到该版本自己的产物目录 `package/PackingProof+v<X.Y.Z>/RELEASE_NOTES_v<X.Y.Z>.md`，在打包生成产物目录之后写入。禁止放在仓库根目录，也禁止提交进仓库；`package/*` 已被 Git 忽略。打包脚本会在文件不存在时按模板生成骨架，在文件已存在时原样保留，正常流程不需要手工改文件名。
- 标题固定为 `v<X.Y.Z> <一句话内容>`，且这句话必须点出本版本最核心的变化（本版投入最大的功能，或用户最痛的问题），例如「采集预览迁移 GPU 与存储判定重做」；只写版本号或只写“体验优化”都会被发布脚本拒绝（未传 `-Title` 直接报错）。
- 《下载与更新说明》必须与本次实际上传的资产逐条对齐：GitHub 的 Setup、Gitee 专属的 no-runtime 安装包、macOS 的 DMG、建立新启动器基线时的 LauncherPatch；少写一项就属于发布不规范，多写未上传的资产、或写已经不存在的分发方式（例如整包 ZIP 免安装包）同样算错。
- 条目顺序按重要性从高到低：本版重点功能 → 重要修复 → 小优化与文案调整；`update_v<X.Y.Z>.json` 的 `notes` 保持同一顺序。同一件事（同一模块的默认值、迁移、提示、界面入口等）必须合并成一条，不允许拆成多条重复描述。预览版需在 GitHub 与 Gitee 标记 prerelease；预览版只写本次增量，正式版汇总上一正式版以来所有预览版。
- `update_vX.Y.Z.json` 的 `title` 与 Release 标题完全一致，`notes` 必须已经填写，发布脚本会直接拒绝“请填写更新标题”这类占位内容。`notes` 是供启动器直接显示的纯文本字符串数组，每项只写一条简洁、用户可见的变化；禁止 Markdown 标题、列表减号、序号、换行排版和“下载某某包更新”等说明。`notes` 按模块归并、控制在 15~20 条以内，但必须覆盖本版本全部用户可见变化，不能只写几条最重要的；超过 20 条发布脚本会拒绝。
- `notes` 面向的是店员等最终用户，只写他们能感知的变化。工程与内部改动一律不写，例如运行时版本、打包与增量包机制、CI 门禁、代码重构、测试补充；这些留在发布笔记的“兼容与工程”里。
- `notes` 每条要短：启动器会折行，但条目过长折成多行后整页看起来又乱又难读。单条建议 20~35 字，硬上限 40 字（含标点），发布脚本会拒绝超长条目。
- 不生成 AppFull 或 ManualUpdate，不上传旧名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`。`launcher_manifest` 和 `release_info` 仅作本地校验交接，默认不上传。

| 目标 | 上传资产 |
| --- | --- |
| GitHub | Setup、update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch；macOS 包 `PackingProof-macOS-<版本>.dmg`（在 Mac 上用 `Tools/Publish-MacRelease.sh` 上传） |
| Gitee `PackingProof/PackingProof-Desktop` | update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch，不上传 Setup；macOS 包 `PackingProof-macOS-<版本>.dmg` 同上 |
| Gitee 专属（每次发布都要传） | `PackingProof_Setup_no-runtime_vX.Y.Z.exe`：不含 .NET 运行时的安装向导（约 60MB），Gitee 用户唯一的"双击安装"入口 —— Gitee 单附件上限 100MB，完整 Setup（自包含应用）约 110MB 传不上去，漏掉它 Gitee 侧就只剩增量包。用 `pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag vX.Y.Z -UploadGitee` 生成并上传；根启动器是 AOT 原生、直接复用正式包里的那一份，`app\` 由该脚本**自己**跑一次 `dotnet publish --self-contained false` 得到（不能再复用正式包的 app，那是自包含的、会把运行时也带进去），并从正式包里补上 `tools\ffmpeg.exe`、WinTts 程序集与 `tts_cache`（这类安装靠机器上的 .NET 10 Desktop Runtime 跑，也没有种子可落共享布局，扩展直接走系统运行时）。AppPatch 本身是自包含形态，这类目录吃到补丁后会变成自包含，往后一切正常。默认不上传 ZIP 免安装包（`PackingProof_no-runtime_vX.Y.Z.zip`），确有需要时加 `-IncludeZip` |

- 完整 7z 与完整 ZIP 都不再上传到任何 Release，默认也不生成，仅在本地确有需要时分别传入 `-IncludeSevenZip` 和 `-IncludeFullZip`。对外分发只有 Setup（Gitee 侧换成 no-runtime 安装向导）、AppPatch、update JSON 与 macOS DMG；普通用户通过启动器自动更新，无需手动下载清单。

- Gitee 发布令牌固定取仓库根目录 `.env` 的 `GITEE_TOKEN`，由脚本注入 `GITEE_TOKEN` 环境变量后交给 CLI；CLI 自己保存的登录态只作回退，而且它按身份字符串各存一份、`gitee auth status` 在令牌失效时仍返回 0，不能用来判断可用性。发布时对 `PackingProof/PackingProof-Desktop` 执行 `gitee release create --repo PackingProof/PackingProof-Desktop --target main` 和 `gitee release upload`；不再向旧个人仓库发布。
- 两个平台的 Release 可由 `pwsh -NoProfile -File Tools/Publish-Releases.ps1 -Title "<一句话内容>" -ConfirmCommitCoverage [-Prerelease]` 一次创建，发布笔记默认取产物目录里的 `RELEASE_NOTES_v<X.Y.Z>.md`（要指别处才传 `-NotesFile`）。脚本要求工作区干净、当前提交有精确 tag，并确认发布笔记已覆盖 `release_commits_v<X.Y.Z>.txt` 的全部提交后才按上面的资产表挑文件上传，GitHub 创建失败会自动重试；已发布版本补写正文用 `-UpdateNotes`，只校验不发布用 `-ValidateOnly`。GitHub 登录态由 gh CLI 维护，Gitee 令牌由脚本从 `.env` 读取后注入环境变量，不打印、不落盘、不提交。

## 重发同一版本（热修已发布版本）

正常发布只跑两条命令：`Publish-CleanPackage.ps1`（出包）→ `Publish-Releases.ps1`（建 Release 传资产）→
`Publish-NoRuntimePackage.ps1 -Tag v<X.Y.Z> -UploadGitee`（Gitee 专属 no-runtime 包）。
**给已经发出去的版本补修复**是另一件事，已经收敛成一条命令（重建 → 替换两边资产 → 重做 no-runtime）：

```powershell
pwsh -NoProfile -File Tools\Republish-SameVersion.ps1 -Version <X.Y.Z> -Title "<一句话内容>" -ConfirmCommitCoverage -Confirmed
```

重发会覆盖两个平台上已发布的资产，属"对外动作"：不加 `-Confirmed` 只打印计划并停下，`-DryRun` 只看计划不报错。
脚本内部按下面的顺序执行，出问题就在对应那步停下：

1. 发布标签必须指向已合并到主干的提交，且修复在里面；HEAD 可以比标签多提交，但只允许工具与文档差异 ——
   应用侧代码（`ExpressPackingMonitoring*`、`Installer`）与标签不一致时脚本直接拒绝，避免发出去的包不是标签内容
2. **清掉产物目录再全量重打**：`Publish-CleanPackage.ps1` 自己会重建产物目录，并保留人工写好的发布笔记。
   脚本结尾有"产物新鲜度守卫"：`app\ExpressPackingMonitoring.dll` 早于应用侧最新源码时直接报错，
   所以复用旧产物这条路已经被堵住（见下方"内容守卫"）
3. 打包结束后核对三处：产物目录里有没有 `PackingProof_LauncherPatch_v<X.Y.Z>.zip`（若本版建立新基线）、
   `RELEASE_NOTES_v<X.Y.Z>.md` 是不是自己写的那份（不是骨架）、`update_v<X.Y.Z>.json` 的 `title`/`notes`/
   `launcher_package` 是不是本次的值
4. 两边替换资产：`Publish-Releases.ps1 -UpdateNotes -ReplaceAssets` —— GitHub 用 `gh release upload --clobber`；
   Gitee 先删同名旧附件再上传（Gitee 不会覆盖同名附件）
5. 重做 Gitee 的 no-runtime 安装包：`Publish-NoRuntimePackage.ps1 -Tag v<X.Y.Z> -UploadGitee`（上传前同样先删同名旧附件）
6. macOS 侧：在 Mac 上 `SIGN_IDENTITY="Developer ID Application: ..." NOTARIZE=1 Tools/Publish-MacRelease.sh <版本> both`。
   **签名必须在 Mac 桌面会话的终端里跑**：SSH 会话拿不到登录钥匙串里的签名私钥，会报 `errSecInternalComponent`

产物目录名带提交后缀（`-2-g<sha>`、`-dirty`），标签外多提交一次、甚至工作区变脏就会换目录名。
所以发布笔记不按目录写死：打包时会在本次产物目录、`package\.release-notes\` 和同版本历史产物目录里
找最新一份人工正文（模板骨架不算），保留时再同步一份到 `package\.release-notes\` —— 换目录名重打也不会
把写好的笔记换成模板

## 发布内容守卫（防止"看着是新的、内容是旧的"）

历史事故：源码已改，产物目录里的 app 负载仍是上一次的旧文件，脚本只报"成功"，于是发出去的包
内容仍是旧的。现在发布链路上有三道内容校验，任何一道不过都直接失败：

1. **产物新鲜度**（`Publish-CleanPackage.ps1`）：`app\ExpressPackingMonitoring.dll` 不得早于应用侧工程
   里最新的 `.cs`/`.xaml`，比源码旧就要求清理产物目录后重新打包
2. **产物一致性**（`Publish-Releases.ps1`）：解开 AppPatch，与产物目录里的
   `ExpressPackingMonitoring.dll` / `ExpressPackingMonitoring.UpdateCore.dll` 做 SHA256 比对，不一致停止发布
3. **启动器补丁存在性**（`Publish-Releases.ps1`）：更新清单声明本版提供 LauncherPatch
   （`launcher_package.version` == 本版版本）时，产物目录里必须有对应 zip，否则停止发布

排查现场"更新不到新版本"时，先用字符串标记确认包内容而不是看时间戳：例如本次修复会在
`ExpressPackingMonitoring.UpdateCore.dll` 里留下 `release list scanned`，而 `per_page=30` 是旧逻辑特征
（Windows 的 AppPatch、macOS 的 DMG 都可以这样验）。这一步已沉淀成可复用工具：

```powershell
pwsh -NoProfile -File Tools\Test-PackageContent.ps1 -PackagePath package\PackingProof+v<X.Y.Z>\PackingProof_AppPatch_v<X.Y.Z>.zip -Require "release list scanned" -Forbid "per_page=30"
```

zip 会自动解开、目录会递归扫 `dll`/`exe`；退出码 0 通过、1 不通过，可直接接进发布门禁或人工核对。

发布出去之后还要对一遍"用户到底看不看得见、下不下得动"：

```powershell
pwsh -NoProfile -File Tools\Test-UpdateReachability.ps1 [-Version <X.Y.Z>] [-AllowLegacyBlind]
```

它对着线上两个平台的 release 复盘：目标版本两边是否都带 `update_v*.json` 与 AppPatch、清单里的
`size`/`sha256` 是否和真实增量包一致、两个平台是否指向同一份内容；再按 0.0.73 / 0.0.74 老客户端的
挑选口径（Gitee 只看第一页且是旧 → 新、GitHub 新 → 旧）模拟一遍挑版本，挑不到就直接失败并打印
"要删哪些老 release、要删哪些老 `update_v*.json`"。只读、不改任何发布内容；等客户端全部升级到
带分页挑选的版本之后，可用 `-AllowLegacyBlind` 放行这一项。
