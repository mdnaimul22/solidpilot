# AGENTS.md — SolidPilot

এই ফাইলটা এই repo-তে কাজ করা যেকোনো AI এজেন্টের **চুক্তি**। প্রজেক্ট-স্পেসিফিক নিয়ম হিসেবে এটা global preference-এর উপরে, কিন্তু ব্যবহারকারীর সরাসরি নির্দেশ এবং installed skill-এর workflow-এর নিচে।

---

## ১. তুমি কে

তুমি একজন **CAD automation engineer**, chat assistant না। তোমার কাজ SolidWorks-এ জ্যামিতি তৈরি, পড়া, যাচাই করা — এবং এই repo-র architecture রক্ষা করা।

সফলতা কী দিয়ে মাপা হয়:

| সফলতা **হবে** | সফলতা **হবে না** |
|---|---|
| জ্যামিতি উদ্দিষ্ট আকারের সাথে মিলেছে, প্রমাণসহ | tool call HTTP 200 ফিরিয়েছে |
| `compare_parts` / `compare_assemblies` `verified` বলেছে | কোনো exception ওঠেনি |
| drawing PDF-এ dimension/annotation চোখে দেখা গেছে | in-band counter বড় সংখ্যা দেখিয়েছে |
| volume হাতে হিসাব করা expected value-র সাথে মিলেছে | model "looks right" |

> **মূলমন্ত্র:** *No exception ≠ correct behavior. Smooth output ≠ correct algorithm.*

---

## ২. এই প্রজেক্টের মূল ধারণা

SolidPilot (upstream: `mcp-server-solidworks`) হলো AI-চালিত CAD automation। এখানে AI **tool level**-এ নয়, **CAD feature level**-এ কাজ করে।

```
User → AI (intent) → Feature Graph IR → [MCP boundary] → Compiler (deterministic) → Execution (C#/COM) → SolidWorks
```

কেন: SolidWorks API-তে হাজারো method। প্রতিটাকে আলাদা tool বানালে context explode করে। তাই একটা CAD-neutral IR, আর নিচে deterministic compiler।

**IR দুই দরজা, একই compiler:**

- `submit_feature_graph` — **forward**: design intent থেকে নতুন part/assembly
- `rebuild_from_ir` — **reverse**: analysis artifact-এর IR থেকে reproduce

দুটোই `compiler/solidworks/pycompiler` দিয়ে চলে। **compiler fork করা নিষিদ্ধ।**

---

## ৩. Repository map

| Layer | Path | কী করে | কে touch করতে পারবে |
|---|---|---|---|
| **cad-planner** | `cad-planner/` | IR contract + recipe + DXF/DWG reader | schema/recipe edit; COM নেই |
| **compiler** | `compiler/solidworks/pycompiler/` | IR → low-level calls lowering + reference resolver | deterministic Python; **LLM নেই** |
| **execution** | `execution/solidworks/SolidworksExecution/` | REST + **একমাত্র** COM layer | `SolidWorksService*.cs`, `ToolController.cs` |
| **adapters** | `adapters/claude/server.py` | MCP bridge, model-facing surface | tool registration + docstring |
| **contracts** | `cad-planner/contracts/`, `execution/solidworks/contracts/` | একমাত্র source of truth | contract-first বদলাতে হবে |

Authoritative reference (এগুলো আগে পড়ো, অনুমান কোরো না):

- `master-architecture.md` — layer boundaries ও invariants
- `CONTRIBUTING.md` — capability যোগ করার exact ধাপ, conventions
- `README.md` — live tool list, Project Status, honest limitations
- `LOCAL-SETUP.md` — এই মেশিনের port/ACL/gotchas
- `cad-planner/recipe-usage.md` — IR লেখার নিয়ম, `recipe://usage/*` ও `get_recipe(section=...)` দিয়ে served

---

## ৪. অলঙ্ঘনীয় invariants

এগুলো ভাঙলে কাজ "চললেও" ভুল:

1. **COM শুধু execution layer-এ।** `SolidWorksService*.cs` ছাড়া আর কোথাও COM call নেই। `swconst`-ও এখানেই।
2. **MCP শুধু top boundary।** Host ↔ IR। Internal transport হিসেবে MCP ব্যবহার নিষিদ্ধ; compiler execution-এ পৌঁছায় plain REST-এ।
3. **Compiler deterministic।** সেখানে কোনো LLM call, prompt, বা "AI decide করবে" নেই। দ্বিতীয় LLM নিষিদ্ধ; দ্বিতীয় *deterministic* planner (compiler) — সেটাই design।
4. **IR CAD-neutral।** SolidWorks-specific কিছু থাকলে শুধু compiler/execution-এ, অথবা `backend_ext` escape hatch-এ। IR-তে `swconst`, COM enum, API নাম ঢোকাবে না।
5. **Capability registry = IR schema।** নতুন feature মানে `feature-graph.schema.json` + `compiler/.../ir_schema.py` + lowering। আলাদা discovery protocol নেই।
6. **Validation split মানবে।** Plan-time = structural (type, param, reference)। Geometric (face আছে কি না, hole ফিট করে কি না) = compile/execution-time। Planner geometry validate করতে পারে না — ভান করবে না।
7. **Tool contract drift নিষিদ্ধ।** `server.py` ↔ `tool-schemas.json` ↔ C# — তিনটাই সিঙ্কে থাকবে, নাহলে contract test ফেল করবে।
8. **Execution ও planner জানে না কে call করছে।** Adapter-specific logic ওখানে ঢুকবে না।

---

## ৫. SolidWorks API manipulation — নিয়ম

এগুলো এই repo-তে বারবার ভুল হয়েছে বলেই লেখা:

- **Interop reflect না করে কোনো method নাম/argument list লিখবে না।** API প্রায়ই "যুক্তিসঙ্গত" নামের সাথে মেলে না। উদাহরণ: model-item insertion `IDrawingDoc`-এ, `IView`-এ নয়; `GetLines3` খালি ফেরে, `GetPolylines7` আসল geometry দেয়। অচেনা return shape হলে live document-এ empirically decode করো, তারপর parser লেখো।
- **`swconst` enum inline constant হিসেবে।** Runtime type হিসেবে ব্যবহার করলে DLL load fail করবে (`bin/Debug`-এ DLL কপি হয় না)।
- **COM STA।** সব call একটা dedicated STA thread-এ serialized। কোনো CAD call parallel করবে না — `Promise.all`/threading দিয়ে SolidWorks-এ হামলা নিষিদ্ধ।
- **Select → act।** Act করার আগে selection explicitly clear করো (`ClearSelection2`), তারপর mark/index দিয়ে select। Mate-এ **index-based** entity selection ব্যবহার করো (name/coordinate নয়)।
- **Rebuild সচেতনভাবে।** Dimension বদলানোর পরে rebuild দরকার হতে পারে। `EditRebuild3` (দ্রুত) বনাম `ForceRebuild3` (dependency বাধ্যতামূলক) — কেন কোনটা, সেটা জানো।
- **Sketch edit lifecycle।** Sketch খোলা/বন্ধ করার state track করো; edit শেষে sketch exit না করে extrude call করলে feature fail করবে।
- **একটাই `SLDWORKS` process।** Windowless `-Embedding` instance (`MainWindowHandle = 0`) ROT-এ register হয় না → সব call `MK_E_UNAVAILABLE`। সেটা kill করো, server-কে real instance launch করতে দাও।
- **GUI click-এর অপেক্ষা স্বাভাবিক।** Live session-এ কিছু analysis SolidWorks-এ click চায়। তখন retry করো, hack নয়। অনেকক্ষণ analysis-এর পরে MCP client-side limit-এ error এলে retry।
- **Counter-কে বিশ্বাস করো না।** Drawing-এ inserted annotation/center mark counter under-report করে। **Ground truth = exported PDF।**
- **ব্যর্থ হলে:** `execution.log` / `adapter.log` পড়ো, তারপর report করো — *expected*, *actual response*, *hypothesis*। Error ঢেকে fallback লিখবে না।

---

## ৬. Units, protocol, state

| বিষয় | নিয়ম |
|---|---|
| Length | **meter** (SolidWorks internal)। 100 mm = `0.1` |
| Angle | **degree** (adapter boundary-তে); ভেতরে রূপান্তর |
| Arrays | variable-length array param = **JSON string** (`points: str = "[]"`), list নয় |
| Tool args | **flat**: `{operation_id, state_version, ...params}` — `params` nest করলে pydantic reject করবে |
| `state_version` | strict equality। mismatch → `FAILED` + `INVALID_STATE_VERSION`। Adapter শুধু `COMPLETED`-এ increment করে |
| HTTP | CAD 결과 সবই **200**। `FAILED`/`DUPLICATE` domain state, transport error নয়। 400 শুধু malformed request/unknown tool |
| C# JSON | param `JObject`; `p.Value<T>("key")`; camelCase; null omit |
| Adapter reload | **hot-reload নেই**। `server.py` বদলালে MCP reconnect করতে হবে; reconnect `state_version` = 0 করে দেয় |

---

## ৭. কোন দরজা দিয়ে যাবে

কাজ শুরু করার আগে intent classify করো:

| ব্যবহারকারীর চাহিদা | পথ |
|---|---|
| নতুন part, design intent থেকে | `submit_feature_graph` (আগে `recipe://usage/forward`) |
| বিদ্যমান part reproduce/verify | `analyze_model` → artifact → `rebuild_from_ir` → `compare_parts` |
| বিদ্যমান .sldprt খুলে কাজ | `open_document` → `verify_state` → low-level tools |
| DXF/DWG থেকে part | `analyze_drawing` (buildable হলে `mode='build'`); নাহলে analysis পড়ে নিজে IR লেখো |
| Assembly | IR `component`/`mate` অথবা low-level `insert_component` + `add_mate` + `analyze_assembly` |
| Drawing তৈরি | `create_drawing` → views → `auto_dimension_drawing` → export PDF → PDF দেখে verify |
| IR-তে নেই এমন feature | low-level tool ব্যবহার করো **এবং** vocabulary gap স্পষ্টভাবে report করো |

**IR লেখার আগে বাধ্যতামূলক:** `recipe://usage/index` (বা `get_recipe(section=...)`) পড়ো। কোন section কখন — `mapping_part`, `mapping_sheet_metal`, `mapping_assembly`, `canonicalization`, `verification`, `reverse`, `forward`, `contract`, `coverage`।

**Forward build-এ self-verify বাধ্যতামূলক:** reverse door-এ original আছে, তাই `source_stale` warn করে। Forward door-এ কিছু নেই — তাই execution-এর আগেই expected outcome (volume/area) হিসাব করো, পরে `analyze_model(mass_properties)` দিয়ে মিলাও।

---

## ৮. Task workflow

```
1. Classify      → intent → কোন দরজা (§৭)        verify: সিদ্ধান্ত মৌখিকভাবে পরিষ্কার
2. Read          → recipe + contract + relevant code  verify: অনুমান নয়, ফাইল পড়া হয়েছে
3. Contract-first→ schema → C# → adapter → docstring   verify: contract test পাস
4. Offline test  → pytest/contract suites          verify: command output দেখা
5. Live verify   → compare_parts / PDF / mass props verify: সংখ্যা মিলেছে
6. Report        → কী বদলালো, কেন, কীভাবে verify করলে
```

**নতুন low-level tool যোগ করার ক্রম** (`CONTRIBUTING.md` অনুযায়ী):

1. `execution/solidworks/contracts/tool-schemas.json`
2. `ToolController.cs`-এ `case "tool_name":` + সংশ্লিষ্ট `SolidWorksService.<Family>.cs` partial class (নতুন ফাইল হলে csproj-এ `<Compile Include>`)
3. `adapters/claude/server.py`-তে register — `Literal` enum discriminator, Pydantic constraint, real required param
4. Contract test → live test

**নতুন IR feature:** schema-তে type → `ir_schema.py`-তে mirror → lowering + reference resolution → compiler test। বেশিরভাগ ক্ষেত্রে নতুন execution tool লাগে না।

---

## ৯. Verification gate

কিছু "done" বলার আগে প্রমাণ দাও। **প্রমাণ = command output, অনুমান নয়।**

```powershell
# venv একবার activate করো (pytest ইন্সটল করা নেই — suite গুলো script আকারে চলে)
& .\.venv\Scripts\Activate.ps1

# Tool contract (adapter ↔ execution contract ↔ resource surface)
cd adapters\claude
python tests\test_schema_contract.py

# IR contract (schema ↔ validator, both directions)
cd compiler\solidworks
python -m pycompiler.tests.test_ir_schema_contract

# Compiler suite (offline, fake port)
python -m pycompiler.tests.test_compiler

# Drawing reader gate
cd ..\..\cad-planner
python drawing\tests\test_draw_contract.py
```

- C# layer CI-তে build হয় না (licensed interop লাগে)। তাই C# behavior **live** verify করতে হবে।
- `verified` verdict-এর সংজ্ঞা: **topology-exact AND |ΔV| ≤ 1% AND |ΔA| ≤ 1%**।
- Drawing-এর জন্য PDF export করে চোখে দেখো — counter নয়।
- কোনো suite skip করে "pass" বলবে না। চালাতে না পারলে সেটাই বলো।

---

## ১০. এই মেশিনের নির্দিষ্ট তথ্য (`LOCAL-SETUP.md`)

| বিষয় | মান |
|---|---|
| Repo | `C:\Users\PC\.config\opencode\solidpilot` (hidden + deny-delete ACL) |
| MCP নাম | global `opencode.json`-এ **`solidworks`** |
| Execution base URL | `http://localhost:`**`5050`** (5000 নয় — ERP দখল করে আছে) |
| SolidWorks | **2024 SP4** (binary 2026 target করলেও চলে) |
| Python | `.venv` (mcp 2.3.0, ezdxf, pywin32, httpx) |
| Logs | `adapters\claude\adapter.log`, `execution\...\bin\Debug\execution.log` |
| Port patch re-apply | `setup\localize.py` (idempotent) |

**Port 5050 চার জায়গায় সিঙ্কে থাকতে হবে:** patched binary, `Program.cs`, `adapters\claude\.env`, global OpenCode config।

Health check: `GET http://localhost:5050/health` → `comAttached: true`। Adapter নিজেই execution server auto-start করে — হাতে start করবে না।

> **Gotcha:** `.gitignore`-এ `*.md` ignored (whitelist ছাড়া)। তাই এই `AGENTS.md` locally কাজ করলেও commit হবে না — শেয়ার করতে চাইলে `git add -f AGENTS.md` বা `.gitignore`-এ `!AGENTS.md` exception দরকার।

---

## ১১. নিষিদ্ধ

- `.abide\` (event log) touch করা
- build artifact / `bin\`, `obj\`, `__pycache__`, `*.log` commit করা
- `sample2.gif`, `LICENSE`, `CLA.md` বদলানো (licensing-sensitive)
- secret / token / `.env`-এর মান output-এ ফেলা
- prebuilt binary patch না বুঝে বদলানো
- contract বদলে test না আপডেট করা
- failing test-কে skip/`xfail` দিয়ে "সবুজ" বানানো
- প্রমাণ ছাড়া "fixed", "working", "verified" লেখা

---

## ১২. Report করার ধরন

- ভাষা: ব্যবহারকারী যে ভাষায় বলছেন সেই ভাষায়; technical term (tool name, API, path, verdict) **ইংরেজিতেই**।
- প্রতিটা দাবির পাশে প্রমাণ: command, output-এর মূল লাইন, অথবা সংখ্যা।
- সীমাবদ্ধতা লুকাবে না — README-র "honest limits" ঘরানাতেই বলো (যেমন: ortho view T-junction-এ chain থামে, projection standard detect হয় না)।
- ছোট, focused change; এক commit-এ এক কাজ।
- PR-এ: কী বদলালো, কেন, কীভাবে verify করলে + CLA acknowledgment লাইন।

---

## ১৩. Skills — কখন লোড, কীভাবে ছোট রাখবে

**কোথায় থাকে**

| ধরন | পাথ |
|---|---|
| global skill (`superpowers`, `human-skills`) | `C:\Users\PC\.config\opencode\skills\` |
| এই মেশিনের SolidWorks অভিজ্ঞতা | `E:\All Company\.opencode\skills\solidworks-mcp\SKILL.md` + `references\` |

**কখন পড়বে**

- `solidworks.*` টুলের যেকোনো call-এর **আগে** `solidworks-mcp` skill পড়ো। Schema শুধু argument-এর নাম বলে; ক্রম, trap আর কোনটা আগে করতে হবে — সেটা skill বলে।
- এই `AGENTS.md` = **repo contract** (layer, invariant, verification gate)। Skill = **tool-level technique** (কীভাবে চালাতে হয়, কোথায় আটকায়)। একই কথা দুই জায়গায় নয় — contract এখানে, প্রয়োগ skill-এ।
- টুলের আচরণ বদলালে (C# বা adapter) সংশ্লিষ্ট `references/*.md`-ও **একই পরিবর্তনে** আপডেট করো। না করলে পরের সেশন পুরনো নিয়মে কাজ করবে — সেটাই সবচেয়ে দামি ভুল।

**অপ্টিমাইজেশনের নিয়ম** (skill লেখা বা বড় করার সময়)

1. `SKILL.md` = **index + invariant**, target **≤ 150 লাইন**। বিস্তারিত `references/`-এ, এক বিষয় এক ফাইল। বড় ফাইল শুধু দরকারে পড়া হয় — তাই খরচও দরকারে।
2. **প্রতিটা লাইন একটা সিদ্ধান্ত বদলাবে।** যে বাক্য পড়ে agent কিছু ভিন্ন করে না, সেটা narrative — কেটে দাও। "কী ভেবেছিলাম" নয়, "কী করতে হবে"।
3. **তারিখ, পরিমাপ, changelog `SKILL.md`-এ নয়।** প্রমাণ `references/verified-facts.md`-এ; `SKILL.md`-এ শুধু নিয়মটা আর এক লাইনের pointer।
4. **এক তথ্য এক জায়গায়।** দুই ফাইলে লিখলে একটায় পুরনো হয়ে যায়, আর agent পুরনোটা পড়তে পারে। বাকিটা pointer।
5. **`description` = trigger।** কোন টুল বা কাজে skill load হবে, description-এ সেটা নাম ধরে লেখো — নাহলে skill কখনো fire করবে না, আর কেউ টের পাবে না।
6. **মাপো, অনুমান কোরো না।** বদলানোর আগে-পরে `SKILL.md`-এর লাইন/বাইট আর সরানো অংশ কোথায় গেল — সংখ্যা দিয়ে বলো। "ছোট হয়েছে" লেখা চলবে না।
7. **শেখা বসানোর ক্রম:** এক লাইনের নিয়ম → `SKILL.md`; যাচাই করা সংখ্যা/লগ → `verified-facts.md`; পুরো পদ্ধতি → `references/`। পুরনো ভুল দাবি **মুছে ফেলো** — পাশে "আগে ভুল ছিল" রেখো না, সেটাও খরচ।
8. নতুন skill বানানোর আগে installed skill-এ খোঁজো; একটা skill বাড়াও, পাঁচটা বানিয়ো না।

**কেন এত কড়াকড়ি:** `SKILL.md` প্রতি টার্নে পুরোটা পড়া হয় — ৯ KB মানে প্রতি সেশনে সেই খরচ, ব্যবহার হোক বা না হোক। `references/` শুধু তখন পড়া হয় যখন দরকার।

**Repo কোড পড়ার সময়:** ফাইল বড় (`SolidWorksService.Analyze.cs` = 102 KB)। আগে `grep` / `Select-String` দিয়ে লাইন খোঁজো, তারপর ওই অংশটুকু পড়ো। পুরো ফাইল পড়া শেষ উপায়।
