# XPact Engine — Legal Review Blockers

Items requiring Simgenics legal review before specific phases can begin. Manager tracks resolution here.

## BLOCKING — must resolve before listed phase

### 1. AutoRTFM clang fork source access [BLOCKS Phase 1 Task 1.0a]

**Question**: Does Simgenics's Unreal source license include access to Epic's AutoRTFM clang fork source (the LLVM compiler pass that emits transactional bytecode for BP opcodes 0x70–0x73)?

**Why it matters**: The pass source is NOT in our UE 5.9.0 checkout — only prebuilt instrumentation binaries at `Engine/Binaries/Win64/UnrealInstrumentation/`. AutoRTFM is load-bearing for the Blueprint VM (cannot be stripped; 260+ refs in Core, 264 in Engine).

**Three possible answers gate decision #27**:
- (27a) **Yes — license includes the pass source** → full LLVM-pass port; budget 3–4 months Phase 1 Task 1.0a.
- (27b) **No source, but redistribution of Epic's prebuilt binaries permitted under our derived-engine license** → vendor binaries as black-box tool invoked by XBT; budget 4–6 weeks.
- (27c) **Neither** → runtime-only library port + manual `Transact{}` wrappers; BP opcodes implemented as runtime-checked transactions; budget 4–6 weeks. **Default-assume 27c for scheduling.**

**Status**: answer is yes.

---

### 2. Engine/Content (Mannequin Manny/Quinn) distribution rights [BLOCKS Phase 5 sample asset]

**Question**: Does Simgenics's Unreal source license permit distributing `Engine/Content/Characters/Mannequins/` (UE5 Manny/Quinn rigged characters) under XPact branding as part of the MVP-1 sample project?

**Why it matters**: MVP-1 sample needs a rigged character. Manny/Quinn match `AnimGraphRuntime` defaults with no retargeting needed.

**Fallback**: If legal denies → commission CC0 character (Mixamo/Quaternius); timeline impact 4–8 weeks if denial during Phase 5; zero impact if resolved by end of Phase 3.

**Status**: answer is yes.

---

### 3. Tech Soft 3D HOOPS Exchange commercial license [BLOCKS Phase 6 CATIA/NX/Creo import]

**Question**: Will Simgenics procure a HOOPS Exchange commercial license to support CATIA, NX, Creo CAD format imports in Phase 6?

**Why it matters**: STEP/IGES are open standards and ship Phase 5 via Epic's CADKernel (no commercial dep). CATIA/NX/Creo require Tech Soft 3D's HOOPS Exchange. Customer demand for these formats is industrial-customer-specific.

**Status**: answer is yes.

---

### 4. libmodbus LGPL-2.1 shared-link feasibility [BLOCKS Phase 7 Modbus support]

**Question**: Can Simgenics ship libmodbus (LGPL-2.1) as a shared-link dependency in XPact's distribution?

**Why it matters**: PlantIO Phase 7 supports OPC-UA (open62541 MPL-2.0, no issue), MQTT (Eclipse Paho EPL/EDL, no issue), and Modbus TCP. Modbus via libmodbus requires shared-link only — static link is incompatible with LGPL-2.1 and XPact's proprietary distribution.

**Alternative**: If shared-link is unacceptable, source an alternative permissively-licensed Modbus library (e.g., commercial license from libmodbus authors).

**Status**:answer is yes we already have one.

---

### 5. Epic AutoRTFM prebuilt binaries Linux availability [CONDITIONAL Phase 6 Linux port]

**Question**: If decision #27b chosen (vendor Epic binaries), does Epic ship AutoRTFM instrumentation binaries for Linux?

**Why it matters**: Phase 6 Linux platform port needs AutoRTFM to compile. If 27b chosen and Linux binaries don't exist, Linux either: (a) builds without AutoRTFM (broken BP VM on Linux), or (b) Linux port deferred to Phase 7+ until alternative resolves.

**Status**: answer is yes. Resolution required only if decision #27 lands on 27b. If 27c (runtime-only), Linux works.

---

## Manager Discipline

- Each item updated as legal review progresses.
- Items 1–2 critical for MVP-1 schedule.
- Manager pings Simgenics legal at Phase 0 start so resolution arrives by Phase 0 end (8–12 wk window).
