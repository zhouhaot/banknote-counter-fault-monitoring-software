# Windows 原生版架构设计

2026-09-06，用户已确认架构基线。需求、WPF 路线及本架构已确认，已进入编码与验证；实际结果见 Windows原生版实施进度.md。旧 architecture_design.md 仅代表 Django 版。

## 技术与交付

采用 C#、WPF、.NET 10 LTS、SQLite、MVVM。目标 Windows 11 x64，具体支持系统版本以实际测试为准。使用 CommunityToolkit.Mvvm、Microsoft.Data.Sqlite、Microsoft.Extensions.DependencyInjection/Logging；CSV 解析库候选 CsvHelper，实施前核验版本和许可证。不引入 Web 服务、WebView、云端和账号系统。

.NET 10 官方支持至 2028-11-14，.NET 8 于 2026-11-10 结束支持，因此不沿用本机现有 .NET 8 作为新项目基线。[官方周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。SDK 和 NuGet 精确补丁版本在实施时核验、锁定；本机目前只核实 SDK 6.0.410/8.0.424，尚未完成 .NET 10 环境配置。

采用 win-x64 自包含目录发布，不裁剪、不使用 AOT；另以 Inno Setup 生成普通用户安装 EXE。安装器是构建工具，版本及许可证在首个切片检查；不自动采购许可证或证书。首版手动安装新版，自包含运行时安全更新需重新构建分发。[发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)、[安装器官方说明](https://jrsoftware.org/isinfo.php)。

## 目录与调用方向

在独立 codex/windows-native 分支的隔离工作区中实施，从完整旧项目基线开始；主工作区本次设计文档按清单转入并核对，不覆盖旧正式材料和运行数据。

```text
desktop/src/MoneyCounter.Desktop/          WPF页面、ViewModel、启动组装
desktop/src/MoneyCounter.Core/             领域规则、DTO、服务契约、错误
desktop/src/MoneyCounter.Infrastructure/   业务服务实现、SQLite、迁移、CSV、备份
desktop/tests/                            单元、真实数据库集成、原生UI测试
desktop/packaging/                        安装器、版本、第三方声明
desktop/scripts/                          构建、验证、发布
desktop/docs/evidence/                    原生版证据索引
```

页面→ViewModel→Core 服务契约；Infrastructure 实现契约并操作数据库和文件，启动入口负责注入。Core 不引用 WPF/SQLite。Infrastructure 按业务模块组织，不为每张表堆叠通用 CRUD 仓储。页面不执行 SQL。

## 模块和服务契约

以下是待实现的内部契约，不是框架现成 API。返回 Task<Result<T>>，查询返回分页 DTO；命令带 OperationId，编辑主数据带 ExpectedRevision，接受 CancellationToken。

| 模块 | 操作 | 必须保持的规则 |
|---|---|---|
| Registry | 新增/编辑型号设备、停用、查询 | 型号组合与资产编号唯一，引用受限 |
| Operations | 记录状态、登记/关闭异常 | 时间唯一、前后读数均不倒退 |
| Maintenance | 转故障、开始处理、维修、关闭 | 单次转换、维修与最终结果是关闭前提 |
| Inventory | 品类、入库、领用、退回、调整、反向更正 | 精确库存、非负、历史不可改、反向引用唯一 |
| Imports | 校验文件、提交批次、查询结果 | 三类CSV、逐行错误、每文件原子提交 |
| Simulation | 创建/重置数据集 | 来源隔离、保护真实记录 |
| Analytics | 概览、寿命、趋势 | 默认排除模拟、缺参数不可计算 |
| Backup | 创建、验证、恢复 | 一致快照、维护模式、失败回退 |

稳定错误包含 DuplicateRecord、InvalidTransition、MonotonicityViolation、InsufficientStock、ImmutableHistory、SourceMismatch、ConcurrentChange、StorageUnavailable、UnsupportedSchema。错误映射到字段或操作提示，意外异常显示关联号，不伪装为空数据或成功。

## 数据模型与事务

核心表：Model、Device、StatusRecord、Anomaly、Fault、Repair、Consumable、InventoryMovement、ImportBatch、SimulationDataset；另加 SchemaMigration（版本/校验和）和 OperationReceipt（命令标识/摘要/已提交结果）。主外键和关系继承旧 PRD；故障保留来源异常、状态、开始与关闭时间、最终结果；流水保留设备、故障、原因与反向引用。

主键和累计读数使用 Int64；时间采用固定精度 UTC，界面北京时间，日期字段单独处理。库存以百分之一单位的整数保存，输入最多两位小数，转换和累计 checked，溢出拒绝；维持旧数量范围，不使用浮点求和。

来源三态与外键组合由 CHECK 约束：MANUAL 两个来源外键为空，CSV 仅 ImportBatchId，SIMULATED 仅 SimulationDatasetId。模拟子记录仅引用同集模拟父记录；人工/CSV 与模拟父子不得交叉引用。

启用外键、WAL、有界 busy timeout。写入使用后台串行队列，在取得写锁后检查业务不变量并提交。唯一键、枚举、来源、数值由数据库再次约束；库存负数用事务和触发器防守，真实历史禁止更新删除。模拟重置只对指定集按引用顺序删除，不关闭全库约束。

读数插入同时检查前一条和后一条；异常转故障是一个事务；关闭故障检查已有维修、最终结果及时间顺序，关闭后维修不可改。领用需设备，故障必须属于同设备；一条流水只能有一次直接反向更正。相同 OperationId/参数返回既有结果，不同参数拒绝；未知提交结果先查回执，不盲目重试。

## 响应性、导入与查询

Microsoft.Data.Sqlite 的异步方法仍同步执行，SQL 在后台执行，连接不跨线程共享，只通过 Dispatcher 更新 UI。[官方限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)。列表每页50条、分页排序、虚拟化；过期筛选响应不得覆盖新结果。保存禁用重复操作。只有事务回滚完成才报告取消，提交完成必须报告成功。

CSV 继承源码 schemas.py 的 5 MiB、10000 行、单元格2000字符和三类固定表头。预览绑定内容哈希，文件变化重验；提交重新校验当前引用及读数，在一个事务内写入。取消或错误全批次回滚。错误导出防止电子表格公式执行。普通文件选择并不授权运行其中任何命令。

## 安装、数据位置与恢复

程序安装至用户 Programs 目录；LocalApplicationData/MoneyCounterMonitor 下分 data、config、logs、backups。卸载默认保留业务数据；不打包旧 .env、真实数据库、日志或测试残留。按用户互斥量限制单实例，重复启动激活已有窗口。

维护模式统一阻止新查询和写入，等待在途操作完成。启动先处理未完成恢复日志，再校验 schema，最后开放业务。已有库损坏不能静默创建空库。

备份使用 SQLite 一致快照，附 manifest：格式/schema/应用版本、时间、大小、SHA-256。先写临时位置并验证再转正，不直接复制活动 WAL 数据库。哈希仅证明完整性，不证明来源。[官方备份说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)。

恢复流程：校验备份大小/哈希/schema、integrity_check、foreign_key_check及业务约束 → 展示信息 → 用户确认覆盖 → 维护模式 → 当前库回退快照 → 关闭全部连接与连接池 → 同盘暂存副本并写阶段日志 → 受控替换，处理旧 WAL/SHM → 重开验证 → 完成。中断后按阶段日志恢复，绝不混用旧 WAL 和新主库；失败保留回退文件并阻止业务写入。

升级前备份，按版本执行迁移并记校验和；失败回滚，过新 schema 拒绝打开。需要降级时使用配套旧程序和升级前快照，不让旧程序强开新库。日志建议5 MiB×5轮转，不记录正文或秘密。

## 旧数据及版本管理

默认新库，不自动修改旧运行库。是否导入旧实际业务数据尚未确认，不阻塞新库开发。若需要迁移，读取旧一致性备份副本、映射到临时新库，核对表数量、ID/时间/来源、逐设备读数、逐耗材库存、故障与维修链，用户选择后才切换。三类CSV不能代替全库迁移。

软件名称和登记版本暂不改动，完成日期和代码行数按实际新交付重新核对。旧材料只作历史档案。源码、安装包、UI截图和手册锁定到同一提交和构建哈希。

用户已授权后续软件代码上传至 GitHub 仓库 zhouhaot/banknote-counter-fault-monitoring-software；规范SSH地址为 git@github.com:zhouhaot/banknote-counter-fault-monitoring-software.git。推送前核对远程内容、diff、忽略规则和凭据/个人资料；上传代码授权不自动涵盖身份资料或真实业务数据，不执行强制推送。

## 实施与验收

| 切片 | 可检查产物 | 重点验证 |
|---|---|---|
| A | 工具链、原生窗口、型号设备保存、初步安装包 | 普通用户安装、中文路径、重启保存 |
| B | 状态异常、故障维修 | 读数历史插入、重复转换、非法关闭 |
| C | 库存流水 | 精度、负库存、幂等、反向更正 |
| D | CSV、模拟、统计 | 原子性、取消、来源隔离、缺失值 |
| E | 备份、恢复、升级 | 损坏校验、替换中断、版本冲突、卸载保留数据 |
| F | 冻结构建与软著材料 | 完整原生流程、同版代码和截图手册 |

继承500台设备、10万业务记录、1万行CSV规模；旧5个浏览器会话改测后台读写与重复命令。性能建议目标：约定测试机冷启动≤5秒、分页查询P95≤1秒，长任务窗口可响应；这些尚未实测。

单元测规则、集成测真实临时 SQLite、UI测 Windows UI Automation，安装在没有开发环境的 Windows VM/测试机完成。当前 CUA 禁用原生桌面操作，原生UI替代工具和干净测试环境仍待建立；不能把浏览器验证当作原生证据。未执行项目标记未验证。

设计自查已覆盖需求、事务、幂等、恢复、线程与材料一致性，非独立评审或运行验证。用户已确认架构及交互稿，现已形成 Windows原生版实施计划.md；工具链、依赖、UI自动化和安装仍待实际验证。
