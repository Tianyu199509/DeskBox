# Music 官方原生包迁移
基线：4ada64bc205358759812b70d891944cdea271c36。工作树 D:\project\w160-music。
本分支只负责 Music；不合入 Glance/Weather 未提交代码，不提交或发布。

## 行为与依赖
- 会话：系统跟随/指定来源、重名消歧、来源消失恢复；SMTC 读取、事件解绑归包。
- 播放：播放/暂停、上一曲/下一曲、进度拖动、随机/循环；保留各来源能力判断。
- 界面：Auto/Cover/Controls/RecordVertical/RecordHorizontal 五种布局，原生路径图标、封面取色、标题滚动、唱片/唱针、音量与来源浮层。
- 设置：UseArtworkBackdrop/EnableCoverHoverMotion/DisplayMode 为包级共享设置。开发期间宿主 MusicSettingsStore 为权威，1.5.0 字段和 schema 10 文件分别处理。
- 音量：只调用 deskbox_native.dll 的 deskbox_music_volume_v1、ABI 2、能力位 1<<5；无托管 COM 回退，不复制 Shell 加载器。
- 宿主边界：窗口/桌面/胶囊/分组留宿主；环境以值读取，包内容通过 ABI v4 和显式事件通信。

## 验证台账

- x64 与 ARM64 均生成 NativeAOT 共享 DLL，并通过 PE 架构及六个 ABI v4 导出检查、package.integrity、Ed25519 开发签名和 manifest 校验。0.1.8 x64 模块 6,989,312 字节，SHA-256 `6ED40C0976C32BD9C148B0F25F3415ED0CB7B0FAA486B10572D7FF132396504E`；ARM64 模块 7,211,520 字节，SHA-256 `DF5CCE35B93D37B273C4A6D2973E54CD4A02BA7DDAB812B3F3B8BD43B09DBF61`。
- 独立 Debug 宿主通过 B1 安装、重新验证和身份绑定加载 x64 包。可控静音 SMTC 会话验证两个包实例同时显示标题和封面，播放、暂停、上一曲、下一曲均到达该测试会话，不控制用户播放器。
- 真实 Rust 音量入口从宿主目录的 `deskbox_native.dll` 加载并通过 ABI 2、能力位和导出校验；系统音量只读值为 0.22。界面只保留一个系统音量 Slider。会话音量 ABI 仍保留给业务层，不在弹出面板重复显示。
- Auto、Cover、Controls、RecordVertical、RecordHorizontal 五种模式在两个实例上实时同步；浅色/深色和中英文切换通过。销毁一个实例不影响另一个，最后实例销毁触发 shutdown 并解除回调，同一模块随后可重新激活。
- 用户确认主布局无误；用户发现的双音量控件已修复。运行时回归增加 `single-system-volume-slider`，当前完整原生冒烟 18/18 通过，日志无 create/COM/shutdown 错误。
- x64 全量测试 3647/3647；MusicPackage Release 编译 0 警告。宿主仍保留既有警告，不能将其归为本包新增问题。

尚未完成的发布证据：ARM64 真机运行、真实鼠标进度拖动、来源菜单和随机/循环的人工交互、应用会话音量 setter、正式发布者密钥、Direct/Store 渠道、1.5.0 覆盖升级及离线恢复。当前产物仅为 Development 签名测试包。

## 公共接入
使用基线已有安装验证、PackageBindingRegistry、ILegacyInstanceMigration 和 HostApi v4。
Glance 分支上的设置订阅、有效主题/性能和进程级模块固定尚未合入本基线；本分支没有复制第二套公共运行时。Music 的宿主组合类使用无功能 token 的基础设施文件名，避免突破功能边界护栏；与 Glance/Weather 集成时应把各官方包注册收敛到同一 composition root。
