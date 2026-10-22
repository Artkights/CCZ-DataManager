# data 管理器知识库

## 结论速览

本知识库记录数据布局证据。一体化编辑器现已整合业务页、资源和迁移，最新功能和边界以根目录 README.md 为准。表定义来自 `config/profiles/ccz-6.5.json`、`ccz-6.6.json` 与运行时 `ProfileAugmentationService`。

## 阅读顺序

1. `00-总览与共性规范.md`
2. `01-Profile系统.md`
3. `02-6.5布局.md`、`03-6.6布局变体.md`、`04-版本差异矩阵.md`
4. 按模块阅读 `05-人物与兵种.md`、`06-策略与宝物.md`、`07-人物与套装特效.md`
5. 修改、交换或排错时阅读 `08-扫描匹配与回退.md`、`09-CSV与历史记录.md`、`10-验证与烟测.md`；四张拓展表详见 [拓展数据实现与验收](../文档/拓展数据实现与验收.md)。
6. 追溯结论时阅读 `90-证据索引.md`

## 证据等级

| 等级 | 含义 |
| --- | --- |
| `BaselineVerified` | 已用 6.5 默认基底确认结构和读写 |
| `VariantVerified` | 已用相应 6.6 本地变体确认 |
| `SourceReviewed` | 已从旧工具的纯数据实现复核，但本工具样本覆盖有限 |
| `InheritedUnverified` | 从共性布局继承，当前样本没有足够的非空语义证据 |

VerificationLevel 描述证据来源，TableCapability 决定读写能力。Editable 开放字段写入；ReadOnly 只读；Unavailable 缺少可靠布局，显示原因且不读取继承地址中的字节。6.6 策略地形和未经验证的 R/S 指定属于 Unavailable。
