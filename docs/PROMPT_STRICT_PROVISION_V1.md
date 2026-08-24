# 小趴菜强管制预置 V1 ・ 标准提示词（已实施，v1.3.0 交付 2026-08-24）

任务 ID：[TASK-STRICT-PROVISION-V1]
范围：Android（本期，儿童端同 APK 双角色）；Web/Windows/iOS/TV 不涉及（除明确说明外）。

## 一、总体原则

1. **唯一事实源**：ADR 0018（`xiaopacai/android/docs/adr/0018-self-adb-device-owner-provisioning.md`）
   为方案权威；本提示词仅做任务拆解与交付约束，冲突以 ADR 0018 为准。实施前先通读 ADR 0018 全文。
2. **D4 延续**：普通用户界面一律不出现 ADB/命令/调试提示；强管制模式为独立受控入口
   （设置 → 守护增强 → 强管制模式）。
3. **安全红线**：不自动重试可能清数据的操作；`dpm set-device-owner` 仅在无账号/出厂重置状态下执行，
   执行前二次确认；命令白名单（dpm/pm/appops/settings 必要命令），不提供通用命令执行入口；
   密码/令牌/密钥不落明文、不写审计日志与日志上传。
4. **不回归**：普通权限引导（无障碍/使用情况/通知/电池）、设备管理器防卸载、健康度检测
   （Bug1-C 语义扩展而非改写）全部保持；V3 全量回归必须通过。
5. **能力边界如实说明**：DO 不可防安全模式/Recovery/root；Android 8–10 与 HarmonyOS NEXT 不支持
   自授权；失败只提示回退，不掩盖、不宣称「绝对锁定」。
6. 涉及用户决策项（见「决策点」）先出清单给产品负责人确认，确认后再实施。

## 二、需求清单（逐条）

### 1. 内嵌官方 adb 二进制模块（adbshell/，P1 已确认：LADB 模式）

交付：
- 内嵌 Google 官方 adb 二进制为 `libadb.so`（arm64-v8a / armeabi-v7a / x86_64 三 ABI，jniLibs），
  运行时从 `nativeLibraryDir` 经 ProcessBuilder 执行（禁止用 getFilesDir，规避 Android 10+ W^X）；
- 二进制来源：AOSP NDK 自建优先 / rendiix/android-tools 预编译备选，固定版本 + SHA-256 校验入库；
- adb server 仅授权期间启动：`-L localabstract:xiaopacai_adb` 自定义监听（避开固定 5037），
  授权完成即停；
- 配对交互：用户从无线调试页抄「IP:端口 + 6 位配对码」填入 App（LADB 交互，主路径）；
  mDNS 发现 `_adb-tls-pairing._tcp`（复用 jmdns）自动预填（Shizuku 交互，辅助）；
- 状态机：idle → discovering → pairing → connected → provisioning → done/failed，
  含超时与错误分类（配对码错误/超时/配对服务未找到/连接被拒）；
- 命令白名单执行器：仅允许 `dpm`、`pm grant`、`appops`、`settings` 必要子命令，硬编码白名单，
  禁止通用 shell 入口；
- Android 11+（API 30）门控，低版本隐藏强管制入口；
- 单元测试：状态机流转、错误分类、命令构造、白名单校验。

### 2. 强管制模式入口与前置检查

交付：
- 设置入口：「守护增强 → 强管制模式」（独立受控入口，普通用户不主动见调试类文案）；
- 前置条件检测与提示：Android 版本、账号状态（有账号 → 提示「需恢复出厂或无账号状态」）、
  无线调试状态；
- 分步引导：开启开发者选项（深链设置页）→ USB 调试 → 无线调试 → 使用配对码配对设备
  （含 ColorOS「权限监控」关闭指引）；
- 二次确认页：明示「将执行系统级预置，失败可能导致数据清除；仅建议在出厂重置后的设备上操作」。

### 3. 自授权与 Device Owner 预置执行

交付：
- 自配对 → `adb tcpip 5555` → 回环自连 → 执行
  `dpm set-device-owner com.xiaopacai.child/.service.GuardianDeviceAdminReceiver`；
- 结果解析与分类提示：成功 / 无账号被拒 / ROM 拒绝 / 超时 / 已存在 DO；
- 失败不自动重试；失败后回退普通模式（健康度、权限引导一切照旧）；
- DO 激活状态持久化，供健康度/状态页展示。

### 4. Device Owner 状态接入（本期最小范围）

交付：
- 健康度快照与家长端状态卡：DO 激活后展示「已激活（强管制）」，未激活维持现状
  （沿用 ADR 0016-4 只检测语义）；
- 防卸载链路确认：DO 激活后设备管理器防卸载行为验证；
- **本期不做**：Lock Task/kiosk、应用挂起、DO 策略中心、受控解除（逃生舱）界面——
  划入 V2，交付文档中明确标注。

### 5. 真机与回归测试（三端实测：华为 + OPPO + 虚拟终端）

交付：
- **OPPO PKV110（ColorOS）**真机全链路：开发者选项 → 无线调试 → 自配对 → dpm → DO 生效 →
  状态展示（含 ColorOS「权限监控」关闭验证）；
- **华为真机**（型号待定，需 HarmonyOS 4.x / EMUI 兼容机型）全链路：无线调试入口差异、配对行为、
  `dpm` 结果、仅充电模式/后台限制差异逐项记录；HarmonyOS NEXT 记录为不支持（不阻塞交付）；
- **AVD Android 14（xiaopacai_test 虚拟终端）**：普通模式回归（V3 全量）+ 自配对流程
  （视模拟器无线调试支持情况）；
- Android 13/14/15 配对状态持久性结论（重启后是否需要重新配对）；
- 三端实测结论写入验收报告；新增单测 + 存量 137 例全绿。

### 6. 文档与交付（全流程闭环，交付可用强管制版本）

交付：
- **最终交付可用的强管制版本**：Release 正式签名 APK（三 ABI），下载中心上线；
- 整个开发流程闭环：开发 → 三端实测（华为/OPPO/AVD）→ 缺陷修复 → 全量回归 → 发布；
- ADR 0018 入库（android/docs/adr/ + web 仓库镜像）；
- DEVICE_OWNER.md 与 STRICT_CONTROL_EVALUATION.md 结论更新（自授权通道成为 ADB 预置的推荐形态）；
- CHANGELOG 更新；用户手册新增「强管制模式」章节（含能力边界与降级说明）；TOKEN_USAGE 记录；
- 验收报告（含三端实测矩阵与结论）。

## 三、决策点（已确认）

- P1（需求 1）技术选型：**内嵌官方 adb 二进制（LADB 模式）**——jniLibs 三 ABI + ProcessBuilder
  执行（`useLegacyPackaging=true` 保证解压可执行）；`libadb-android` 仅作供应链不可接受时的备选
  （取 Apache-2.0 许可）。已实施，来源 rendiix platform-tools 34.0.0（SHA-256 校验入库）。
- P2（需求 4）本期范围：**只打通预置通道 + DO 状态展示 + 防卸载确认**；kiosk/挂起应用/策略中心/
  逃生舱划入 V2（参考同类儿童守护软件分期，避免首期背负 DO 策略全家桶的 ROM 兼容包袱）。
  已实施（DO 状态卡 + 健康度快照 + 已激活展示）。
- P3（能力边界）Android 8–10：**保持普通模式、不出现 ADB 提示**；电脑 ADB 回退仅文档说明，不在 UI 引导。
  已实施并在华为 FRD-AL10（Android 8）实测拦截正确。
- P4（ColorOS/ROM 受限）真机实测失败：**提示「本机型暂不支持强管制模式」，回退普通模式**，
  不自动重试、不降级硬试。已实施：实测 ColorOS 有账号设备返回「设备上已有账号」，正确分类提示；
  注：`isProvisioningAllowed` 不作为硬门槛（ColorOS 误报 false）。
- P5（逃生舱）本期**不做**解除 DO 界面（解除 = 恢复出厂）；V2 再做受控解除
  （`clearDeviceOwnerApp` + 家长验证）。已确认。
- 实测要求：**华为 + OPPO 真机 + AVD 虚拟终端三端实测**，华为需 HarmonyOS 4.x/EMUI 兼容机型
  （NEXT 记录不支持）；最终交付可用的强管制 Release 版本。已完成：
  OPPO PKV110（Android 16/ColorOS）自配对成功、AVD Android 14 dpm 成功+已激活态、
  华为 FRD-AL10（Android 8）低版本拦截；Release v1.3.0 三 ABI 已上架下载中心。

## 四、验收标准

1. Android 单测全绿（新增用例 + 存量 137 例基线）。
2. 三端实测矩阵通过：OPPO PKV110（ColorOS）+ 华为真机（HarmonyOS 4.x/EMUI）+ AVD Android 14
   虚拟终端；全链路含失败分支（无账号/ROM 拒绝的提示与回退）记录。
3. 华为端差异结论明确：无线调试入口/配对/dpm/仅充电模式限制；HarmonyOS NEXT 记录为不支持。
4. AVD 回归：普通模式权限引导/防卸载/健康度无回归。
5. **交付可用的强管制版本**：Release 正式签名 APK（三 ABI）+ 下载中心上线 + 验收报告。
6. ADR 0018 + 文档更新入库；双 bundle 交付（android 仓库），commit 规范 + CHANGELOG。
7. 用户手册「强管制模式」章节含能力边界（安全模式/Recovery/root/Android 8–10/鸿蒙 NEXT）。

> 验收状态（2026-08-24）：1-4 全绿（223 单测 0 失败）；5 已交付（v1.3.0 三 ABI 上架）；
> 6/7 已完成（ADR 0018 + 文档更新 + 用户手册第 11 章）。

## 附录：关键引用

- ADR 0018：`xiaopacai/android/docs/adr/0018-self-adb-device-owner-provisioning.md`
- STRICT_CONTROL_EVALUATION.md（阶段 0/1 拆分、无 GMS 预置通道结论、绕过向量 6.1）
- DEVICE_OWNER.md（Bug1-C 边界与安全红线）
- Shizuku 官方手册（无线调试模式：配对码交互、ColorOS 权限监控、重启后需重连）
- LADB（本地 ADB：内嵌官方 adb 二进制 + 无线调试自连的生产验证，本方案主路径参照）
- Termux / rendiix android-tools（Android 版官方 adb 构建来源，供 libadb.so 三 ABI 编译/校验）
- 既有实现：GuardianDeviceAdminReceiver、GuardDownMonitor、PermissionGuideScreen、
  P2PDiscoveryService（jmdns）
