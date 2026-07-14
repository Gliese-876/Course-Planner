# Course Planner JSON 导入教程

本教程对应交换格式 `2.0.0`，完整说明课程库与选课方案两类 JSON 的编写、检查和导入方法。仓库中的示例已通过当前导入器的预览与应用验证。

[English](./json-import-guide.en.md) · [课程库示例](./examples/course-library.json) · [方案示例](./examples/selection-plan.json) · [字段参考](#字段参考) · [常见错误](#常见错误)

> [!IMPORTANT]
> JSON 交换文件不是数据库备份。若要迁移应用的全部状态，请使用设置页中的“备份与恢复”。

## 1. 最稳妥的编写流程

1. 在应用中先建立一个学期，并配置节次时间与所需标签。
2. 从规划页工具栏选择“导出”，导出课程库或当前方案，得到与当前版本完全一致的模板。
3. 使用 UTF-8 编辑器修改模板；保留所有字段，即使某个可空字段的值是 `null`。
4. 如果修改了课程身份字段，运行仓库提供的 ID 检查脚本并修正 `offeringId`。
5. 在规划页选择“导入”，检查预览中的新增、更新、跳过、冲突与警告。
6. 只有在预览符合预期时才应用。方案包若携带本地不存在的课程，需要选择“同步课程并应用”。

也可以从这两个经过验证的文件开始：

- [`course-library.json`](./examples/course-library.json)：1 个学期、4 个标签、3 门课程。
- [`selection-plan.json`](./examples/selection-plan.json)：1 个方案及其引用的 2 门课程。

## 2. 选择正确的包类型

| 目标 | `kind` | 根对象必须包含 | 适合场景 |
|---|---|---|---|
| 导入课程资料 | `courseLibrary` | `semesters`、`labels`、`courses` | 批量建立学期、标签与课程库 |
| 导入一个选课方案 | `selectionPlan` | `semester`、`labels`、`courses`、`plan` | 分享一个方案及其依赖课程 |

两类包都必须使用 `"schemaVersion": "2.0.0"`。`kind` 或版本不匹配时，导入器不会猜测格式。

## 3. JSON 基本规则

- 根节点必须是 JSON 对象，文件编码为 UTF-8。
- 属性名采用区分大小写的 camelCase：`courseName` 正确，`CourseName` 不正确。
- 不允许注释、尾随逗号、重复属性名、`NaN` 或 `Infinity`。
- 未知的附加字段会被忽略，但不要依赖它们保存数据。
- 当前交换 DTO 的所有已知字段都必须出现。可空字段应显式写成 `null`，不能省略。
- 空集合写成 `[]`，空文本写成 `""`；不要用 `null` 代替集合或非空文本字段。
- 日期使用 `YYYY-MM-DD`，时间使用 `HH:mm:ss`，时间戳使用带时区的 ISO 8601，例如 `2026-07-14T09:00:00+08:00`。
- 枚举使用数字，不使用英文枚举名。完整映射见[枚举值](#枚举值)。

## 4. 两种根结构

以下片段只展示结构；可直接导入的完整内容请使用仓库示例。

```json
{
  "kind": "courseLibrary",
  "schemaVersion": "2.0.0",
  "semesters": [],
  "labels": [],
  "courses": []
}
```

```json
{
  "kind": "selectionPlan",
  "schemaVersion": "2.0.0",
  "semester": {},
  "labels": [],
  "courses": [],
  "plan": {}
}
```

## 5. 课程 ID 是强校验字段

`offeringId` 不能随意填写。导入器会重新计算课程身份，并要求结果完全相同。身份输入包括：

- 课程的 `semesterId`、`courseName`、`teacher` 与 `location`；
- 按星期、起止节次、单双周和周次表达式排序后的 `meetingTimes`；
- 文本会先去除首尾空白、把连续空白折叠为一个空格，并执行 Unicode NFC 规范化。

规范化后的身份对象会序列化为确定性的 JSON，再计算 SHA-256，结果是 64 位小写十六进制字符串。学分、标签、备注、容量和颜色不参与 ID，但改变上述身份字段中的任意一个都会改变 ID。

在仓库根目录运行：

```powershell
pwsh ./scripts/Get-CourseOfferingId.ps1 ./docs/examples/course-library.json
pwsh ./scripts/Get-CourseOfferingId.ps1 ./docs/examples/selection-plan.json
```

把每一行的 `ExpectedOfferingId` 复制到课程的 `offeringId`。对于方案包，还要同时更新对应快照的 `courseOfferingId`。重新运行，直到所有 `Matches` 都为 `True`。

如果不想处理哈希，最简单的方法是在应用中创建课程后导出模板，只修改不影响身份的字段。

## 6. 关键一致性规则

- `semesterId` 必须在学期、课程、方案之间完全一致。
- 学期名称与方案名称必须能作为 Windows 文件名使用：避免 `< > : " / \ | ? *`、控制字符、末尾空格或句点，以及 `CON`、`NUL` 等保留名。
- 学期 `endDate` 必须与 `startDate`、`weekCount`、`weekStartDay` 计算出的完整周范围一致。
- 节次必须从 1 连续编号，结束时间晚于开始时间，且相邻节次不能重叠。
- `weekday` 始终是 1=周一到 7=周日；`weekStartDay` 只影响周视图的起始列。
- 课程的普通 `labels`、`courseGroupType`、`studyType` 都必须在根 `labels` 目录中存在，并使用正确的标签种类。
- 同一课程内的普通标签不能空白或重复；同一包内的标签名按规范化文本进行不区分大小写的唯一性检查。
- 方案的每个 `courseOfferingId` 必须引用随包携带或本地已有的课程。
- `registrationOrder` 必须出现，并在当前方案包中形成不重复、连续的 `0..N-1`；可空类型不代表可导入值可以是 `null`。
- 同一方案不能重复引用同一课程，`snapshotId` 也必须唯一。

## 7. 在应用中导入

1. 打开规划页，选择工具栏中的“导入”。
2. 选择 JSON 文件。应用会先检查大小、语法、模式与业务规则，不会立即修改数据。
3. 阅读预览：
   - “新增”会创建对象；
   - “更新”会替换相同身份的对象；
   - “跳过”不会改变现有对象；
   - “冲突”需要处理学期设置或同名对象；
   - “警告”常见于超出学期范围的周次或节次。
4. 导入方案时，如果预览提示缺少课程，选择“同步课程并应用”；否则方案无法引用这些课程。
5. 只有在明确了解后果时才使用“强制合并学期设置”或“强制导入越界课程”。
6. 应用后检查周课表、冲突提示与抢课顺序。

## 8. 导入前检查清单

- [ ] `kind` 和 `schemaVersion` 完全正确。
- [ ] 所有必需字段均存在，可空字段也未被省略。
- [ ] 枚举是表中允许的数字。
- [ ] 学期日期、周数和节次表一致。
- [ ] 标签引用存在且种类正确。
- [ ] `Get-CourseOfferingId.ps1` 输出全部 `Matches = True`。
- [ ] 方案快照引用正确，`registrationOrder` 连续。
- [ ] 文件在应用预览中没有意外冲突或警告。

## 字段参考

### 根对象：`courseLibrary`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `kind` | string | 固定为 `courseLibrary` |
| `schemaVersion` | string | 当前固定为 `2.0.0` |
| `semesters` | Semester[] | 包内学期目录 |
| `labels` | CourseLabel[] | 包内完整标签目录 |
| `courses` | CourseOffering[] | 课程目录 |

### 根对象：`selectionPlan`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `kind` | string | 固定为 `selectionPlan` |
| `schemaVersion` | string | 当前固定为 `2.0.0` |
| `semester` | Semester | 方案所属学期 |
| `labels` | CourseLabel[] | 所携课程引用到的标签 |
| `courses` | CourseOffering[] | 方案需要的课程 |
| `plan` | SelectionPlan | 方案与快照 |

### `Semester`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `semesterId` | string | 非空、包内唯一；课程与方案必须引用它 |
| `semesterName` | string | 非空、规范化后唯一、合法 Windows 文件名组件，最长 255 |
| `startDate` | date | `YYYY-MM-DD`，年份 1900–2100 |
| `endDate` | date | 与周数和每周起始日一致 |
| `weekCount` | integer | 1–60 |
| `weekStartDay` | enum number | 0=周一，1=周日 |
| `displayOrder` | integer | 显示排序键 |
| `periodSchedule` | PeriodDefinition[] | 至少 1 项，最多 128 项 |

### `PeriodDefinition`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `period` | integer | 从 1 连续编号 |
| `start` | time | `HH:mm:ss` |
| `end` | time | 晚于 `start`，并且不与下一节重叠 |

### `CourseLabel`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `name` | string | 非空；规范化后不区分大小写地唯一 |
| `kind` | enum number | 0=普通标签，1=课程组类型，2=修读类型 |
| `displayOrder` | integer | 显示排序键 |

### `CourseOffering`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `offeringId` | string | 课程身份 SHA-256；64 位小写十六进制 |
| `semesterId` | string | 必须引用包内学期 |
| `courseName` | string | 非空，最长 2048 个 UTF-16 代码单元 |
| `teacher` | string | 可为空字符串，但不能省略或为 `null` |
| `location` | string | 可为空字符串，但不能省略或为 `null` |
| `credits` | number | 0–100 |
| `courseGroupType` | string or null | 引用 `kind=1` 标签；字段必须出现 |
| `studyType` | string or null | 引用 `kind=2` 标签；字段必须出现 |
| `labels` | string[] | 仅引用 `kind=0` 标签；最多 128 项 |
| `meetingTimes` | MeetingTime[] | 每门课最多 32 项 |
| `notes` | string | 可为空字符串，最长 2048 |
| `enrolledCount` | integer or null | 0–1,000,000；字段必须出现 |
| `capacity` | integer or null | 0–1,000,000；字段必须出现 |
| `color` | string | `#RRGGBB` |
| `modifiedAt` | timestamp | 带时区的 ISO 8601 |

### `MeetingTime`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `weekday` | integer | 1=周一 … 7=周日 |
| `startPeriod` | integer | 至少为 1 |
| `endPeriod` | integer | 不小于开始节次；通常不超过学期节次数 |
| `weeks` | string | 例如 `1-16`、`1,3,5-8`；最长 1024 |
| `weekParity` | enum number | 0=全部，1=单周，2=双周 |

同一课程在相同星期与周次上的节次区间不能互相重叠。`weekParity` 会过滤 `weeks` 展开的周次。

### `SelectionPlan`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `planId` | string | 非空 |
| `semesterId` | string | 必须等于 `semester.semesterId` |
| `planName` | string | 非空、合法 Windows 文件名组件，最长 255 |
| `displayOrder` | integer | 显示排序键 |
| `createdAt` | timestamp | 带时区的 ISO 8601 |
| `modifiedAt` | timestamp | 带时区的 ISO 8601 |
| `snapshots` | PlanCourseSnapshot[] | 最多 5000 项 |

### `PlanCourseSnapshot`

| 字段 | 类型 | 约束与含义 |
|---|---|---|
| `snapshotId` | string | 非空且方案内唯一 |
| `courseOfferingId` | string | 引用 `courses[].offeringId` 或本地同一课程 |
| `registrationOrder` | integer | 字段必需；全体必须是连续的 `0..N-1` |
| `snapshotAt` | timestamp | 带时区的 ISO 8601 |

## 枚举值

| 字段 | 0 | 1 | 2 |
|---|---|---|---|
| `weekStartDay` | 周一 | 周日 | — |
| `CourseLabel.kind` | 普通标签 | 课程组类型 | 修读类型 |
| `weekParity` | 全部 | 单周 | 双周 |

## 验证限制

这些是当前 `2.0.0` 实现的安全边界，未来模式版本可能调整。

| 项目 | 上限或范围 |
|---|---:|
| 文件字节数、输入字符数 | 各 64 MiB |
| JSON 深度 | 64 |
| 每个对象的属性数 | 64 |
| JSON token 数 | 5,000,000 |
| 任意数组的项目数 | 5,000 |
| 学期数 | 128 |
| 标签数 | 512 |
| 课程数 | 5,000 |
| 每学期节次数 | 128 |
| 每课程普通标签数 | 128 |
| 每课程上课时间数 | 32 |
| 每方案快照数 | 5,000 |
| 每方案上课时间行数 | 2,000 |
| 包内标签引用总数 | 100,000 |
| 聚合文本字符数 | 5,000,000 |
| 普通文本字段 | 2,048 个 UTF-16 代码单元 |
| 学期名与方案名 | 255 个 UTF-16 代码单元 |
| 模式版本文本 | 64 个 UTF-16 代码单元 |
| 学期周数 | 1–60 |
| 支持日期 | 1900-01-01 … 2100-12-31 |
| 学分 | 0–100 |
| 人数与容量 | 0–1,000,000 |
| `weeks` 表达式 | 1,024 个 UTF-16 代码单元 |

## 常见错误

| 现象 | 常见原因 | 处理方法 |
|---|---|---|
| 未知 JSON 类型 | `kind` 拼写或大小写错误 | 使用精确的 `courseLibrary` 或 `selectionPlan` |
| 无效 JSON | 缺字段、错误的 `null`、枚举越界、重复属性或 ID 不匹配 | 与字段表逐项比对，并运行 ID 检查脚本 |
| 版本不支持 | `schemaVersion` 不是 `2.0.0` | 从当前应用重新导出模板 |
| 包过大 | 超过数量、文本或文件限制 | 拆分课程库，缩短文本 |
| 学期日期与周数不一致 | `endDate` 不是完整周范围的末日 | 在应用创建学期后导出其日期 |
| 节次或周次越界 | 课程引用了不存在的节次或周 | 修正课程，或确认预览后谨慎强制 |
| 标签缺失或种类错误 | 课程引用未在根 `labels` 中声明 | 添加对应标签并设置正确 `kind` |
| 课程 ID 无效 | 身份字段改变后未更新哈希 | 运行 `Get-CourseOfferingId.ps1` |
| 方案不能应用 | 本地缺课程，或快照顺序/引用不一致 | 选择“同步课程并应用”，并检查快照 |
| 名称含非法字符 | 学期名或方案名含 `/`、`:` 等 | 改用破折号、圆点或中点等安全字符 |

如果仍无法定位问题，先从应用导出一个最小模板，只保留一门课程，确认可导入后再逐项增加内容。
