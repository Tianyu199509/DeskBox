# DeskBox Licensing & Commercial Terms

**English** | [简体中文](#简体中文)

---

## English

**Copyright © 2026 Tianyu Zhu (朱天雨). All rights reserved.**

Starting with **DeskBox 1.6.0**, DeskBox adopts a **Dual-Licensing Model**: the open-source distribution is licensed under **`GPL-3.0-only`**, while commercial licensing options are offered directly by the copyright holder for proprietary, OEM, and commercial distribution use cases.

---

### 1. Dual-Licensing Model (`GPL-3.0-only` + Commercial License)

The DeskBox source code repository is available under two licensing tracks:

1. **Open-Source License (`GPL-3.0-only`)**:
   - Governed by the GNU General Public License version 3.0 only ([LICENSE](LICENSE)).
   - You may use, study, modify, and redistribute the GPL-licensed source code and derivative works in full compliance with `GPL-3.0-only`.
   - Any distributed modification or software integrating DeskBox source code under this track must release its complete corresponding source code under `GPL-3.0-only` and preserve all copyright and license notices.
2. **DeskBox Commercial License**:
   - Required if you wish to:
     - Distribute modified versions of DeskBox or integrate DeskBox source code into proprietary / closed-source software without complying with `GPL-3.0-only` source disclosure obligations;
     - Pre-install, OEM-bundle, white-label, or commercially distribute DeskBox in hardware, enterprise software suites, or commercial packages;
     - Enter into enterprise procurement, customized development, or commercial redistribution agreements.
   - As the sole copyright holder, Tianyu Zhu explicitly reserves the right to publish official paid editions (including the **Microsoft Store** edition) and to ship proprietary closed-source modules, **Open-Core** add-ons, cloud synchronization services, or AI-powered hosted services alongside or within official DeskBox releases (such as DeskBox 1.7+ commercial capabilities).

**Commercial Licensing Contact:**

- Rights Holder: **Tianyu Zhu (朱天雨)**
- Email: **1047078635@qq.com** (please include `[DeskBox Commercial]` in the subject line)
- Official Website: <https://deskbox.fun>

---

### 2. Official Prebuilt Binary Releases

Official prebuilt binaries published by the copyright holder include:

- **Direct Installers** (`DeskBox_Setup_*.exe` distributed via [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases) and <https://deskbox.fun>);
- **Microsoft Store Edition** (`MSIX` distributed via Microsoft Partner Center).

Terms for official binaries:

- **Free Desktop Use**: Both personal users and company/enterprise employees may download, install, and run unmodified official Direct installers on desktop PCs for daily personal or internal workplace use without per-seat license fees for standard features.
- **Package Manager Indexing**: Reputable package managers (such as `winget`, `Scoop`, and similar catalog tools) are permitted to index and reference the unmodified official download URLs and SHA-256 checksums without bundling or modifying the installer payload.
- **Prohibited Actions**: You may **not**:
  - Repackage, modify, wrap with third-party downloaders, adware, or installers, or mirror modified installers as official DeskBox packages;
  - Resell unmodified or repackaged official installers for profit on third-party marketplaces, auction platforms, or paid download portals (such as Xianyu, Taobao, or commercial download aggregators) without written authorization;
  - Present unofficial or modified builds as official DeskBox releases.

---

### 3. Custom Widgets & Creator Exemption (`.dbxwidget`)

DeskBox 1.6.0 introduces the declarative **Custom Widget** engine, `.dbxwidget` packages, sandboxed QuickJS scripting, `deskbox://` deep links, and `--mcp` / Studio bridges.

To encourage an open and thriving creator ecosystem, **Tianyu Zhu explicitly grants a copyleft boundary clarification / exemption for independent user-created and third-party widgets**:

- Independent `.dbxwidget` packages, standalone `manifest.json` declarations, sandboxed QuickJS scripts, and external tools or bridges that merely adhere to DeskBox's documented Custom Widget Schema, QuickJS sandbox capability APIs, `deskbox://` deep-link protocol, or `--mcp` / Studio protocol interfaces are treated as **independent works** belonging to their respective creators.
- Creating, sharing, selling, keeping private, or open-sourcing such independent `.dbxwidget` packages or external integrations under **any license of the author's choice** (MIT, Apache-2.0, proprietary, commercial, etc.) does **not** trigger `GPL-3.0-only` copyleft obligations on your independent widget code or external software, provided you do not incorporate or statically/derivatively embed DeskBox's own `GPL-3.0-only` source code.

---

### 4. Official Templates, AI Prompts & Branding Reservation

#### 4.1 Official Templates & Remote Catalog (`catalog/`)

Built-in widget templates (`src/DeskBox/Assets/CustomWidgets/Templates/`) and official remote catalog definitions (`catalog/widgets.v1.json` and `catalog/prompts.v1.json`, including AI generation system prompts and design rules) are original works covered by DeskBox's **`GPL-3.0-only` + Commercial Dual-Licensing** terms:

- Users may inspect, study, and customize built-in or catalog templates for personal use inside DeskBox or within compliant `GPL-3.0-only` open-source projects.
- Competing commercial or closed-source products may **not** scrape, copy, bundle, or repurpose DeskBox's official widget templates or `catalog/prompts.v1.json` prompt engineering rules without a Commercial License.

#### 4.2 Trademarks & Brand Assets (Excluded from GPLv3)

In accordance with **Section 7(e) of GNU GPLv3**, the `GPL-3.0-only` license does **not** grant permission to use the trade names, trademarks, service marks, or product names of the licensor:

- The **`DeskBox`** name, logos and icons (`deskbox.ico`, `deskbox.svg`, `deskbox-mark-*`, `deskbox-tray-*`), official Store/promotional imagery, website copy, and official service endpoints under **`deskbox.fun`** (update checks, remote widget catalog, AI prompt OTA, and feedback endpoints) remain the exclusive property of **Tianyu Zhu**.
- Anyone redistributing modified versions of DeskBox under `GPL-3.0-only` **must**:
  1. Replace the `DeskBox` name and all DeskBox logos, icons, and branding assets with distinct branding;
  2. Clearly state that the build is an unofficial modified derivative and not affiliated with or endorsed by Tianyu Zhu;
  3. Remove or replace official `deskbox.fun` update, catalog, and feedback endpoints so unofficial forks do not send requests to DeskBox's official infrastructure.

---

### 5. Third-Party Open-Source Components

DeskBox incorporates and interacts with third-party open-source libraries, runtimes, and assets (including .NET, Windows App SDK, CommunityToolkit, Markdig, WinUIEx, QuickJS, Rust `windows` crate, Meteocons, Fluent Emoji, and Everything IPC SDK definitions). Each third-party component remains governed by its respective license notice in the repository.

---

### 6. License History, Non-Retroactivity & Disclaimer

- **Early Versions (MIT)**: DeskBox releases and source revisions originally published under the MIT License remain available under the MIT License.
- **Pre-1.6.0 GPL Releases (through 1.5.9)**: Releases published under single-track `GPL-3.0-only` prior to 1.6.0 remain governed by `GPL-3.0-only`.
- **DeskBox 1.6.0 and Later**: Dual-licensed under `GPL-3.0-only` and the DeskBox Commercial License as described in this document.
- License updates are **not retroactive** and do not revoke permissions already granted for earlier releases.

**Warranty Disclaimer & Limitation of Liability:**  
Unless explicitly agreed otherwise in a signed written Commercial License agreement, DeskBox and all related assets are provided **"AS IS"**, without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, and non-infringement. In no event shall the author or copyright holder be liable for any claim, damages, data loss, or other liability arising out of or in connection with the use or inability to use the software.

---

## 简体中文

**版权所有 © 2026 朱天雨 (Tianyu Zhu)。保留所有权利。**

自 **DeskBox 1.6.0** 版本起，DeskBox 采用 **「GPL-3.0-only + 商业授权」双授权模式**：开源侧继续保留完整的 `GPL-3.0-only` 自由软件权益，同时由著作权人对外提供商业授权选项，并明确划定官方发行版、1.6.0 自定义组件工坊生态与品牌商标边界。

---

### 一、双授权总则（`GPL-3.0-only` + 商业授权）

DeskBox 源代码仓库在以下两条授权轨道下同时提供：

1. **开源授权（`GPL-3.0-only`）**：
   - 遵循 GNU 通用公共许可证第 3 版（仅 v3 版本，详见 [LICENSE](LICENSE)）。
   - 只要完全遵守 `GPL-3.0-only` 的全部义务，你可以自由使用、学习、修改和再分发 DeskBox 源码及其衍生作品。
   - 若基于本轨道分发修改版或将 DeskBox 源码集成到其他软件中，必须同步以 `GPL-3.0-only` 公开全部对应源代码，并完整保留版权与许可声明。
2. **商业授权（DeskBox Commercial License）**：
   - 以下场景必须向著作权人取得书面商业授权：
     - 希望在**不履行 `GPL-3.0-only` 源码公开义务**的前提下，分发 DeskBox 修改版，或将 DeskBox 源码集成进闭源 / 专有商业软件；
     - OEM 预装、企业软件套装捆绑、白标贴牌（White-Label）或闭源商业分发；
     - 企业级采购定制开发、私有部署或特定商业合作授权。
   - 作为 DeskBox 唯一著作权人，朱天雨明确保留在官方发行版中提供付费渠道（包括 **Microsoft Store 付费版**），以及在未来官方版本（如 1.7+）中引入闭源 **Open-Core** 增值模块、云同步服务或托管 AI 服务的权利。

**商业授权与合作联系：**

- 权利主体：**朱天雨 (Tianyu Zhu)**
- 联系邮箱：**1047078635@qq.com**（邮件标题请注明 `[DeskBox Commercial]`）
- 官方网站：<https://deskbox.fun>

---

### 二、官方预编译发行版条款

由著作权人直接发布的官方预编译发行版包括：

- **官网 / GitHub 直装包**：通过 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases) 与 <https://deskbox.fun> 发布的 `DeskBox_Setup_*.exe`；
- **Microsoft Store 商店版**：通过 Microsoft Partner Center 发布的 `MSIX` 商店包。

官方发行版适用以下条款：

- **个人与办公桌面免费使用**：无论是个人用户还是企业/机构员工，在桌面电脑上下载、安装并运行未经修改的官方直装包用于个人日常或公司内部日常办公，当前基础功能均**免收按席位人头费**。
- **正规包管理器收录**：允许 `winget`、`Scoop` 等正规开源包管理器原样索引并指向官方下载链接与 `.sha256` 校验哈希（不得篡改或重新打包安装器载荷）。
- **明确禁止的行为**：未经著作权人书面许可，**严禁**：
  - 对官方安装包进行二次打包、夹带推广软件、捆绑下载器或篡改安装内容；
  - 在闲鱼、淘宝、第三方下载站或付费资源站倒卖未经授权的 DeskBox 安装包或伪装成官方付费版进行牟利；
  - 将第三方修改版、非官方构建冒充为 DeskBox 官方发行版。

---

### 三、1.6.0 自定义组件与创作者豁免声明（`.dbxwidget`）

DeskBox 1.6.0 新增了原生声明式**自定义组件引擎**、`.dbxwidget` 组件包、QuickJS 沙盒脚本、`deskbox://` 深链及 `--mcp` / 工坊桥接协议。

为保护社区与第三方组件作者的创作自由，**著作权人特此作出以下免传染豁免声明**：

- 用户或第三方作者独立编写的 `.dbxwidget` 组件包、声明式 `manifest.json`、运行在沙盒中的 QuickJS 脚本，以及仅通过公开文档化的 DeskBox 自定义组件 Schema、QuickJS 能力模块接口、`deskbox://` 协议或 `--mcp` / 工坊接口与 DeskBox 交互的外部程序，均属于创作者的**独立作品**。
- 只要未直接复制或嵌入 DeskBox 本身的 `GPL-3.0-only` 源码，上述独立 `.dbxwidget` 组件与外部工具**不受 DeskBox 主程序 `GPL-3.0-only` 传染条款约束**——作者可自由选择以 MIT、Apache-2.0、闭源私有或任何商业方式发布、分发或自用。

---

### 四、官方模板目录、AI 提示词与品牌商标保留

#### 4.1 官方模板库与远程目录（`catalog/`）

仓库内置的官方组件模板（`src/DeskBox/Assets/CustomWidgets/Templates/`）与远程目录资产（`catalog/widgets.v1.json` 及包含 AI 生成规则与提示词体系的 `catalog/prompts.v1.json`）均属于 **`GPL-3.0-only` + 商业双授权** 保护范围：

- 用户可在 DeskBox 内或合规的 `GPL-3.0-only` 开源项目中自由使用、学习与改写这些模板；
- 未经商业授权，任何竞品或闭源商业软件不得抓取、复制或直接内置 DeskBox 的官方模板库与 `catalog/prompts.v1.json` 提示词规则。

#### 4.2 品牌标识与官方端点保留（依据 GPLv3 第 7(e) 条）

依据 **GNU GPLv3 第 7(e) 条**，本开源协议**不授予**使用许可方商号、商标、服务标识或品牌资产的权利：

- **`DeskBox`** 名称、Logo 与各类应用图标（`deskbox.ico`、`deskbox.svg`、`deskbox-mark-*`、`deskbox-tray-*`）、官方宣传截图、官网文案，以及 **`deskbox.fun`** 下的官方服务端点（更新检查、远程组件目录、AI 提示词 OTA 与反馈接口）均为 **朱天雨 (Tianyu Zhu)** 独占保留的品牌与运营资产。
- 任何依据 `GPL-3.0-only` 修改并重新分发 DeskBox 衍生版本的第三方，**必须同时满足**：
  1. 更换项目名称并移除全部 `DeskBox` Logo、图标与官方品牌标识；
  2. 明确声明其为非官方衍生版本，与原作者及 DeskBox 官方无关联或背书关系；
  3. 移除或替换指向 `deskbox.fun` 的官方更新、目录与反馈服务端点，避免向官方基础设施发起非官方流量。

---

### 五、第三方开源组件声明

DeskBox 使用或交互的第三方组件与资源（包括 .NET、Windows App SDK、CommunityToolkit、Markdig、WinUIEx、QuickJS、Rust `windows` crate、Meteocons、Fluent Emoji 及 Everything IPC 定义等）继续遵循各自原始开源协议与仓库内保留的许可声明。

---

### 六、历史协议演进、不追溯原则与免责声明

- **早期 MIT 版本**：早先已按 MIT 协议公开发布的 DeskBox 历史版本及对应提交记录，继续适用原 MIT 协议。
- **1.6.0 之前（含 1.5.9）的 GPL 版本**：在 1.6.0 之前已按单一 `GPL-3.0-only` 发布的版本，继续按单一 `GPL-3.0-only` 适用。
- **DeskBox 1.6.0 及后续版本**：适用本文档所述的「`GPL-3.0-only` + 商业授权」双授权体系。
- 历次协议调整均**不具有追溯力**，不会撤销用户依历史版本原协议已合法获得的权利。

**免责与责任限制声明：**  
除非在双方签署的书面《商业授权协议》中另有明确约定，本软件及相关资产均按**「原样」（AS IS）**提供，不附带任何明示或暗示的担保（包括但不限于适销性、特定用途适用性及不侵权担保）。在任何情况下，作者或著作权人均不对因使用或无法使用本软件而引发的任何直接或间接损害、数据丢失或其他责任承担赔偿义务。
