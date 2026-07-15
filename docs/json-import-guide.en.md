# Course Planner JSON Import Guide

This guide covers exchange schema `2.0.0` and explains how to author, verify, and import both course-library and selection-plan JSON packages. The repository samples have been validated through the current importer's preview and apply paths.

[Chinese guide](./json-import-guide.md) · [Course-library example](./examples/course-library.json) · [Selection-plan example](./examples/selection-plan.json) · [Field reference](#field-reference) · [Troubleshooting](#troubleshooting)

> [!IMPORTANT]
> Exchange JSON is not a complete database backup. Use Backup and Restore in Settings when you need to move all application state.

## 1. Recommended authoring workflow

1. Create a semester in the app and configure its periods and labels.
2. Use Export on the Planner toolbar to export a course library or the current plan. This gives you a template produced by the same app version.
3. Edit the template in a UTF-8 editor. Keep every field, including nullable fields whose value is `null`.
4. If you change course identity fields, run the repository ID checker and update `offeringId`.
5. Choose Import on the Planner toolbar and review every Added, Updated, Skipped, Conflict, and Warning item.
6. Import only when the merge-style confirmation summary matches your intent. If a plan carries courses that are missing locally, choose “Sync courses and import plan”.

You can also start from these validated samples:

- [`course-library.json`](./examples/course-library.json): one semester, four labels, and three courses.
- [`selection-plan.json`](./examples/selection-plan.json): one plan with its two referenced courses.

## 2. Choose the package type

| Goal | `kind` | Required root members | Best for |
|---|---|---|---|
| Import course data | `courseLibrary` | `semesters`, `labels`, `courses` | Bulk semester, label, and library setup |
| Import one plan | `selectionPlan` | `semester`, `labels`, `courses`, `plan` | Sharing a plan together with its dependencies |

Both package types require `"schemaVersion": "2.0.0"`. The importer does not guess when `kind` or the schema version differs.

## 3. JSON rules

- The root must be a JSON object encoded as UTF-8.
- Property names are case-sensitive camelCase: `courseName` is valid; `CourseName` is not.
- Comments, trailing commas, duplicate property names, `NaN`, and `Infinity` are rejected.
- Unknown additive properties are ignored, but do not use them as durable storage.
- Every known exchange field is required. Write nullable fields explicitly as `null` instead of omitting them.
- Use `[]` for an empty collection and `""` for empty text. Collections and non-null text fields cannot be `null`.
- Dates use `YYYY-MM-DD`, times use `HH:mm:ss`, and timestamps use ISO 8601 with an offset, such as `2026-07-14T09:00:00+08:00`.
- Enums are numeric. Do not write enum names. See [Enum values](#enum-values).

## 4. Root shapes

These snippets show structure only. Use the linked repository examples for complete, importable packages.

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

## 5. Course IDs are validated

`offeringId` is not arbitrary. The importer recomputes it and requires an exact match. Identity inputs are:

- `semesterId`, `courseName`, `teacher`, and `location`;
- `meetingTimes` sorted by weekday, start period, end period, parity, and weeks expression;
- identity text trimmed, Unicode NFC-normalized, and reduced to single spaces between runs of whitespace.

The canonical identity JSON is hashed with SHA-256, producing 64 lowercase hexadecimal characters. Credits, labels, notes, enrollment, capacity, and color do not affect the ID. Any identity-field change does.

From the repository root, run:

```powershell
pwsh ./scripts/Get-CourseOfferingId.ps1 ./docs/examples/course-library.json
pwsh ./scripts/Get-CourseOfferingId.ps1 ./docs/examples/selection-plan.json
```

Copy `ExpectedOfferingId` into each course's `offeringId`. For a plan package, update the matching snapshot `courseOfferingId` too. Run the command again until every `Matches` value is `True`.

The easiest alternative is to create the course in the app, export it, and edit only non-identity fields.

## 6. Cross-field invariants

- `semesterId` must match across the semester, courses, and plan.
- Semester and plan names must be valid Windows file-name components. Avoid `< > : " / \ | ? *`, control characters, trailing spaces or periods, and reserved names such as `CON` or `NUL`.
- `endDate` must match the complete-week range implied by `startDate`, `weekCount`, and `weekStartDay`.
- Periods start at 1, remain consecutive, end after they start, and do not overlap.
- `weekday` is always 1=Monday through 7=Sunday. `weekStartDay` only controls calendar ordering.
- Ordinary `labels`, `courseGroupType`, and `studyType` must exist in the package label catalog with the corresponding label kind.
- Ordinary labels on a course cannot be blank or duplicated. Catalog names are unique after normalized, case-insensitive comparison.
- Every plan `courseOfferingId` must resolve to a bundled or existing local course.
- `registrationOrder` must be present and form a unique, contiguous `0..N-1` sequence. Although its JSON type is nullable, `null` is not valid in an importable current-schema plan.
- A plan cannot reference the same course twice; `snapshotId` values must also be unique.

## 7. Import in the app

1. Open Planner and choose Import from the toolbar.
2. Select the JSON file. The app checks size, syntax, schema, and domain rules before changing data.
3. Review the merge-style summary in the confirmation dialog. It lists parse results, conflicts, warnings, and errors together without extra filters.
4. For a plan that includes missing local courses, choose “Sync courses and import plan”. The app synchronizes those dependencies in the same confirmed import without another prompt.
5. Use forced semester merging or forced out-of-range import only when you understand the previewed consequences.
6. After applying, inspect the timetable, conflicts, and registration order.

## 8. Pre-import checklist

- [ ] `kind` and `schemaVersion` are exact.
- [ ] Every required field is present, including nullable fields.
- [ ] Every enum uses an allowed numeric value.
- [ ] Semester dates, week count, and periods agree.
- [ ] Label references exist with the correct kinds.
- [ ] The ID checker reports `Matches = True` for every course.
- [ ] Plan snapshots resolve and `registrationOrder` is contiguous.
- [ ] The app preview contains no unintended conflict or warning.

<a id="field-reference"></a>

## Field reference

### Root object: `courseLibrary`

| Field | Type | Constraints and meaning |
|---|---|---|
| `kind` | string | Exactly `courseLibrary` |
| `schemaVersion` | string | Currently exactly `2.0.0` |
| `semesters` | Semester[] | Semester catalog in the package |
| `labels` | CourseLabel[] | Complete package label catalog |
| `courses` | CourseOffering[] | Course catalog |

### Root object: `selectionPlan`

| Field | Type | Constraints and meaning |
|---|---|---|
| `kind` | string | Exactly `selectionPlan` |
| `schemaVersion` | string | Currently exactly `2.0.0` |
| `semester` | Semester | Owning semester |
| `labels` | CourseLabel[] | Labels referenced by bundled courses |
| `courses` | CourseOffering[] | Courses required by the plan |
| `plan` | SelectionPlan | Plan and snapshots |

### `Semester`

| Field | Type | Constraints and meaning |
|---|---|---|
| `semesterId` | string | Nonblank and unique in the package; courses and the plan reference it |
| `semesterName` | string | Nonblank, normalized-unique, file-name safe, maximum 255 |
| `startDate` | date | `YYYY-MM-DD`, year 1900–2100 |
| `endDate` | date | Must agree with week count and week start |
| `weekCount` | integer | 1–60 |
| `weekStartDay` | enum number | 0=Monday, 1=Sunday |
| `displayOrder` | integer | Display sort key |
| `periodSchedule` | PeriodDefinition[] | 1–128 entries |

### `PeriodDefinition`

| Field | Type | Constraints and meaning |
|---|---|---|
| `period` | integer | Consecutive numbering starting at 1 |
| `start` | time | `HH:mm:ss` |
| `end` | time | After `start`; must not overlap the next period |

### `CourseLabel`

| Field | Type | Constraints and meaning |
|---|---|---|
| `name` | string | Nonblank and unique after normalized, case-insensitive comparison |
| `kind` | enum number | 0=ordinary, 1=course-group type, 2=study type |
| `displayOrder` | integer | Display sort key |

### `CourseOffering`

| Field | Type | Constraints and meaning |
|---|---|---|
| `offeringId` | string | Canonical 64-character lowercase SHA-256 identity |
| `semesterId` | string | References the package semester |
| `courseName` | string | Nonblank, maximum 2048 UTF-16 code units |
| `teacher` | string | Empty is allowed; cannot be `null` or omitted |
| `location` | string | Empty is allowed; cannot be `null` or omitted |
| `credits` | number | 0–100 |
| `courseGroupType` | string or null | References a `kind=1` label; field is required |
| `studyType` | string or null | References a `kind=2` label; field is required |
| `labels` | string[] | References only `kind=0` labels; maximum 128 |
| `meetingTimes` | MeetingTime[] | Maximum 32 per course |
| `notes` | string | Empty is allowed; maximum 2048 |
| `enrolledCount` | integer or null | 0–1,000,000; field is required |
| `capacity` | integer or null | 0–1,000,000; field is required |
| `color` | string | `#RRGGBB` |
| `modifiedAt` | timestamp | ISO 8601 with an offset |

### `MeetingTime`

| Field | Type | Constraints and meaning |
|---|---|---|
| `weekday` | integer | 1=Monday … 7=Sunday |
| `startPeriod` | integer | At least 1 |
| `endPeriod` | integer | At least the start period; normally within the semester schedule |
| `weeks` | string | For example `1-16` or `1,3,5-8`; maximum 1024 |
| `weekParity` | enum number | 0=all, 1=odd, 2=even |

Meeting intervals for the same course may not overlap on the same weekday and week. `weekParity` filters the weeks expanded from `weeks`.

### `SelectionPlan`

| Field | Type | Constraints and meaning |
|---|---|---|
| `planId` | string | Nonblank |
| `semesterId` | string | Must equal `semester.semesterId` |
| `planName` | string | Nonblank, file-name safe, maximum 255 |
| `displayOrder` | integer | Display sort key |
| `createdAt` | timestamp | ISO 8601 with an offset |
| `modifiedAt` | timestamp | ISO 8601 with an offset |
| `snapshots` | PlanCourseSnapshot[] | Maximum 5000 |

### `PlanCourseSnapshot`

| Field | Type | Constraints and meaning |
|---|---|---|
| `snapshotId` | string | Nonblank and unique in the plan |
| `courseOfferingId` | string | References `courses[].offeringId` or the same local course |
| `registrationOrder` | integer | Required; all values form contiguous `0..N-1` |
| `snapshotAt` | timestamp | ISO 8601 with an offset |

<a id="enum-values"></a>

## Enum values

| Field | 0 | 1 | 2 |
|---|---|---|---|
| `weekStartDay` | Monday | Sunday | — |
| `CourseLabel.kind` | Ordinary | CourseGroupType | StudyType |
| `weekParity` | All | Odd | Even |

## Validation limits

These are safety limits in the current `2.0.0` implementation and may change in a future schema.

| Item | Limit or range |
|---|---:|
| File bytes and input characters | 64 MiB each |
| JSON depth | 64 |
| Properties per object | 64 |
| JSON tokens | 5,000,000 |
| Items in any array | 5,000 |
| Semesters | 128 |
| Labels | 512 |
| Courses | 5,000 |
| Periods per semester | 128 |
| Ordinary labels per course | 128 |
| Meetings per course | 32 |
| Snapshots per plan | 5,000 |
| Meeting rows per plan | 2,000 |
| Total label references | 100,000 |
| Aggregate text characters | 5,000,000 |
| Normal text field | 2,048 UTF-16 code units |
| Semester and plan name | 255 UTF-16 code units |
| Schema-version text | 64 UTF-16 code units |
| Semester weeks | 1–60 |
| Supported dates | 1900-01-01 … 2100-12-31 |
| Credits | 0–100 |
| Enrollment and capacity | 0–1,000,000 |
| `weeks` expression | 1,024 UTF-16 code units |

<a id="troubleshooting"></a>

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Unknown JSON kind | Incorrect `kind` spelling or casing | Use exactly `courseLibrary` or `selectionPlan` |
| Invalid JSON | Missing fields, invalid `null`, enum out of range, duplicate property, or ID mismatch | Compare against the field tables and run the ID checker |
| Unsupported schema | `schemaVersion` is not `2.0.0` | Export a new template from the current app |
| Package too large | A count, text, or file limit was exceeded | Split the course library or shorten text |
| Date-week mismatch | `endDate` is not the end of the complete-week range | Create the semester in the app and export its dates |
| Period or week out of range | A course references a missing period or week | Correct the course, or force only after reviewing the preview |
| Label missing or wrong kind | A course reference is absent from root `labels` | Add the label with the correct `kind` |
| Invalid offering ID | Identity fields changed without updating the hash | Run `Get-CourseOfferingId.ps1` |
| Plan cannot apply | Local courses are missing or snapshot order/references are inconsistent | Choose “Sync courses and import plan” and inspect snapshots |
| Illegal name | A semester or plan name contains `/`, `:`, or another reserved character | Use a dash, period, middle dot, or another safe character |

If the issue remains unclear, export a minimal template from the app, keep one course, verify that it imports, and then add data incrementally.
