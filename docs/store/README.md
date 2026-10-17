# Microsoft Store 上架手册

这份手册对应仓库里的商店版 MSIX 构建。提交前先完成 Partner Center 的账号与名称预留，拿到真实的
`Identity Name` 和 `Publisher`，否则只能生成用于本机验证的占位包。

## 1. Partner Center 账号与应用名

1. 打开 [Partner Center](https://partner.microsoft.com/dashboard)，用个人 Microsoft 账号登录。
2. 创建 Individual 或 Company developer account。个人账号用个人 Microsoft 账号；公司账号需要公司域名邮箱和营业执照等证明。
3. 进入 Apps & Games，选择 Reserve app name，预留 `PackingProof` 或 `PackingProof 快递打包监控`。
4. 在应用身份页面复制：
   - Identity Name（形如 `PackingProof.PackingProof`）
   - Publisher（形如 `CN=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）

`Publisher` 必须和 MSIX 清单里的值完全一致。商店提交不需要 CA 代码签名证书；可以使用自签名证书，认证通过后 Microsoft 会重新签名。

## 2. 生成本地身份配置

```powershell
Copy-Item tools\store-package.example.json tools\store-package.local.json
```

编辑 `tools\store-package.local.json`，填入 Partner Center 的两个值。该文件已在 `.gitignore` 中，不会被提交。

## 3. 构建商店包

```powershell
pwsh -NoProfile -File tools\Publish-StorePackage.ps1 -Version 0.0.85 -RequireStoreIdentity -Sign
```

产物在 `package\store\PackingProof_Store_v0.0.85\`：

- `PackingProof_Store_v0.0.85.msix`：提交到 Partner Center 的文件
- `payload\`：解包后的应用负载，便于核对

`-Sign` 会用 `Publisher` 作为证书主题生成自签名证书，再给 MSIX 签名；Microsoft Store 在认证通过后替换为 Microsoft 证书。
如果 Partner Center 的上传校验允许未签名包，也可以去掉 `-Sign`。

应用版本 `0.0.85` 会映射为商店包版本 `1.0.85.0`。商店包版本第 1 段不能为 0，后续版本要保持递增。

## 4. 本机验证（可选，但建议做）

先构建并签名：

```powershell
pwsh -NoProfile -File tools\Publish-StorePackage.ps1 -Version 0.0.85 -Sign -TrustCertificate
```

安装和启动需要管理员权限，因为测试证书要写入 `LocalMachine\TrustedPeople`：

```powershell
Start-Process pwsh -Verb RunAs -ArgumentList @(
  '-NoProfile','-File','tools\Test-StorePackage.ps1',
  '-PackagePath','package\store\PackingProof_Store_v0.0.85\PackingProof_Store_v0.0.85.msix',
  '-CertificatePath','package\store\PackingProof_Store_v0.0.85\PackingProof-test.cer',
  '-Launch'
)
```

验证完清理测试包和证书：

```powershell
Start-Process pwsh -Verb RunAs -ArgumentList @(
  '-NoProfile','-File','tools\Test-StorePackage.ps1',
  '-PackagePath','package\store\PackingProof_Store_v0.0.85\PackingProof_Store_v0.0.85.msix',
  '-Uninstall'
)
```

已验证的行为：按包身份启动、数据写入 `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState`、更新检查跳过、
设置页提示由 Microsoft Store 管理更新、启动项通过 MSIX `startupTask` 注册。

## 5. 商店版与 Setup 版的差异

- 商店版不运行根启动器和 AppPatch 自更新，更新由 Microsoft Store 负责。
- 商店版数据在包自己的 LocalState 中，和 Setup 版的 `%LOCALAPPDATA%\ExpressPackingMonitoring` 分开。
- 开机自启不在注册表 `Run` 中写值，用户在 Windows 设置 → 应用 → 启动 中开启。
- 支持 x64，最低 Windows 10 2004（10.0.19041）。

## 6. Partner Center 提交字段

- 价格：免费
- 市场：全部
- 分类：Business / Productivity（以 Partner Center 实际下拉项为准）
- 年龄分级：按问卷选择，通常为 Everyone / 3+
- 隐私政策：使用 [privacy-policy.html](privacy-policy.html) 部署后的公开地址
- 支持：`https://github.com/PackingProof/PackingProof-Desktop/issues`
- 截图：`docs/store/Listing/Screenshots/` 下的 4 张 PNG
- 认证备注：参考 `docs/store/Listing/notes-for-certification.md`
- 许可证：AGPL-3.0，建议在商店许可证条款中链接仓库 `LICENSE`

## 7. 认证风险提示

- 应用是 `runFullTrust` WPF 桌面程序，包含摄像头/麦克风、局域网 Web 服务、可选的防火墙和 URL ACL 配置。
  提交时在认证备注里说明这些能力只在用户启用对应功能时使用。
- 扩展市场会下载并安装用户选择的脚本或外部适配器。认证备注要说明扩展是独立进程/脚本、由用户主动安装并经过签名与权限校验。
  如果认证反馈把它视为动态代码分发，商店版需要先关闭扩展市场，再重新提交。
- 本地 Web 服务首次启用局域网访问时会弹一次 UAC；不开局域网时应用仍可正常录像和回放。

## 8. 商店文案与截图

- 中文文案：`docs/store/Listing/zh-CN.md`
- 英文文案：`docs/store/Listing/en-US.md`
- 认证备注：`docs/store/Listing/notes-for-certification.md`
- 截图重新生成：`pwsh -NoProfile -File tools\New-StoreScreenshots.ps1`

截图来自仓库 `docs/images/` 下的真实界面。上传前请确认画面里的快递单号、订单信息和设备名可以公开展示；如不能，
先替换源图再重新生成。
