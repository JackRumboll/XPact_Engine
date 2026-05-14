# Plugin Disposition Manifest — XPact Engine

Per architectural decision #55, this manifest enumerates every `.uplugin` in the UE 5.9.0 source tree with XPact's disposition.

**Status: TEMPLATE — full 103-row content authored by manager pre-Phase-4a-deps (1–2 weeks read-only triage).**

## Schema

Each row:

| Column | Allowed values | Meaning |
|---|---|---|
| Plugin Name | string | `.uplugin` `FriendlyName` or filename |
| Location | path | repo-relative path to `.uplugin` file |
| Upstream EnabledByDefault | true/false | as shipped by Epic in 5.9.0 |
| XPact Disposition | PORT / STUB / DISABLE / DEFER / EXCLUDE | what we do |
| Target Phase | "0", "1", "2", "3", "4a-deps", "4a-shell", "4a-property", "4a-blueprint", "4b-prereqs", "4b", "4c", "5", "6", "7+", "N/A" | when we act |
| Rationale | one-line | why this disposition |
| XPact Plugin Name | string (or empty) | if PORT/STUB, name in XPact (often X-prefix) |

## Disposition Definitions

- **PORT**: Full faithful port. Subagent task produced. Ships with engine.
- **STUB**: Compile-only stub (empty module exporting just enough to satisfy linker). Ships with engine but runtime no-op.
- **DISABLE**: `.uplugin` rewritten with `EnabledByDefault: false`. Module not even compiled in standard build. May be ported later by flipping the flag.
- **DEFER**: Same as DISABLE but with a documented Phase 6/7+ revival path.
- **EXCLUDE**: NOT PORTED. Source files removed from the XPact tree. No revival path planned.

## Decided-now Rows (locked architectural decisions)

| Plugin | Location | Upstream | Disposition | Target Phase | Rationale | XPact Name |
|---|---|---|---|---|---|---|
| EnhancedInput | `Engine/Plugins/EnhancedInput/` | true | PORT | 3 (runtime), 4a-shell (EnhancedInputEditor) | Standard modern UE input system (decisions #11, #57) | XEnhancedInput |
| Datasmith | `Engine/Plugins/Enterprise/DatasmithImporter/` | true | PORT | 5 | STEP/IGES CAD import (decision #11, #31) | XDatasmith |
| Datasmith CAD Importer | `Engine/Plugins/Enterprise/DatasmithCADImporter/` | true | PORT | 5 | 30+ sub-modules; CADKernel-driven | XDatasmithCADImporter |
| Lidar Point Cloud | `Engine/Plugins/Enterprise/LidarPointCloud/` | true | PORT | 5 | Plant scan visualization (decision #31) | XLidarPointCloud |
| Variant Manager | `Engine/Plugins/Enterprise/VariantManager/` | true | PORT | 5 | Scenario state variants (decision #31) | XVariantManager |
| Variant Manager Content | `Engine/Plugins/Enterprise/VariantManagerContent/` | true | PORT | 5 | runtime side | XVariantManagerContent |
| Dataprep Editor | `Engine/Plugins/Enterprise/DataprepEditor/` | true | PORT | 5 | CAD cleanup pipeline (decision #31) | XDataprepEditor |
| Dataprep Geometry Operations | `Engine/Plugins/Experimental/Enterprise/DataprepGeometryOperations/` | true | PORT | 5 | Dataprep dep | XDataprepGeometryOperations |
| Concert | `Engine/Plugins/Developer/Concert/` | true | PORT | 6 | Multi-User Editing (decision #31) | XConcertSync, XMultiUserClient, etc. |
| Datasmith Runtime | `Engine/Plugins/Experimental/Enterprise/DatasmithRuntime/` | true | PORT | 6 | In-game CAD import | XDatasmithRuntime |
| Niagara | `Engine/Plugins/FX/Niagara/` | true | PORT | 4c | VFX for industrial scenes (decision #44, 8 sub-modules) | XNiagara |
| OpenXR | `Engine/Plugins/Runtime/OpenXR/` | true | PORT | 6 | VR training (decision #21) | XOpenXR |
| XRBase | `Engine/Plugins/Runtime/XRBase/` | true | PORT | 6 | OpenXR dep (decision #21) | XXRBase |
| MetaSounds | `Engine/Plugins/Runtime/Metasound/` | true | PORT | 4c (editor), 5 (runtime) | Full 6-module port (decision #10) | XMetasound |
| Significance Manager | `Engine/Plugins/Runtime/SignificanceManager/` | true | PORT | 5 | Active-set perf for static actors (decision #66) | XSignificanceManager |
| Smart Objects | `Engine/Plugins/Runtime/SmartObjects/` | true | DEFER | 6 | Industrial procedure-object modeling (decision #65) | XSmartObjects |
| Game Features | `Engine/Plugins/Runtime/GameFeatures/` | true | DEFER | 6 | Modular scenarios (decision #61) | XGameFeatures |
| Modular Gameplay | `Engine/Plugins/Runtime/ModularGameplay/` | true | DEFER | 6 | GameFeatures dep | XModularGameplay |
| State Tree | `Engine/Plugins/Runtime/StateTree/` | true | DEFER | 6 | Procedure modeling alternative (decision #61) | XStateTree |
| StructUtils (Experimental) | `Engine/Plugins/Experimental/StructUtils/` | true | PORT | 4a-deps | UnrealEd trans dep; in-CoreUObject part Phase 1 (decision #56) | XStructUtils |
| Live Coding | `Engine/Plugins/Developer/LiveCoding/` | true | STUB | 4a-shell | C# uses ALC; C++ LiveCoding deferred (decision #41) | XLiveCoding-stub |
| Common UI | `Engine/Plugins/Runtime/CommonUI/` | true | DEFER | 7+ | UMG sufficient for MVP (decision #66) | — |
| Synthesis | `Engine/Plugins/Runtime/Synthesis/` | true | DEFER | 7+ | Procedural audio not MVP (decision #66) | — |
| Audio Synesthesia | `Engine/Plugins/Runtime/AudioSynesthesia/` | true | DEFER | 7+ | Same (decision #66) | — |
| nDisplay | `Engine/Plugins/Runtime/nDisplay/` | true | DEFER | 7+ | Multi-display (decision #22) | — |
| Zone Graph | `Engine/Plugins/Runtime/ZoneGraph/` | true | DEFER | 7+ | AI nav lanes (decision #65) | — |
| Gameplay Behaviors | `Engine/Plugins/Experimental/GameplayBehaviors/` | true | DEFER | 7+ | (decision #65) | — |
| PCG | `Engine/Plugins/PCG/` | true | DEFER | 7+ | Procedural content (decision #64) | — |
| Mass Entity / Mass family | `Engine/Plugins/Runtime/MassEntity/`, related | true | DEFER | 7+ | High-density crowd ECS (decision #63) | — |
| AnimNext family (7+ plugins) | `Engine/Plugins/Experimental/AnimNext*/` | true | DEFER | 7+ | Next-gen anim; ControlRig dep (decision #64) | — |
| Control Rig | `Engine/Plugins/Animation/ControlRig/` | true | DISABLE | 7+ | Decision #40 | — |
| Control Rig Modules | `Engine/Plugins/Animation/ControlRigModules/` | true | DISABLE | 7+ | Decision #40 | — |
| Control Rig Spline | `Engine/Plugins/Animation/ControlRigSpline/` | true | DISABLE | 7+ | Decision #40 | — |
| IK Rig | `Engine/Plugins/Animation/IKRig/` | true | DISABLE | 7+ | Decision #40 | — |
| Animation Data | `Engine/Plugins/Animation/AnimationData/` | true | DISABLE | 7+ | Requires ControlRig (decision #40) | — |
| Rig VM | `Engine/Plugins/Runtime/RigVM/` | true | DISABLE | 7+ | Decision #40 | — |
| Rig Logic | `Engine/Plugins/Animation/RigLogic/` | true | EXCLUDE | N/A | MetaHuman-only | — |
| LiveLink (all 13 sub-plugins) | `Engine/Plugins/Animation/LiveLink*`, `Engine/Plugins/MovieScene/LiveLink*` | true | EXCLUDE | N/A | Film/mocap; not training/sim (decision #40) | — |
| Meta Human / Mutable / Bridge / Megascans / Fab | various | true | EXCLUDE | N/A | Marketplace integrations | — |
| Chaos (all sub-plugins) | `Engine/Plugins/Runtime/Chaos*` | true | EXCLUDE | N/A | PhysX 5 chosen (decision #9) | — |
| Verse Compiler / Verse VM / Verse Interop | various | true | EXCLUDE | N/A | Decision #26 | — |
| Movie Render Pipeline | `Engine/Plugins/MovieScene/MovieRenderPipeline/` | true | DEFER | 7+ | Cinematic export (decision #22) | — |
| Python Script Plugin | `Engine/Plugins/Experimental/PythonScriptPlugin/` | false | EXCLUDE | N/A | C# is our scripting; Python not needed | — |
| Editor Scripting Utilities | `Engine/Plugins/Editor/EditorScriptingUtilities/` | true | PORT | 4b | C# editor parity (decision #32) | XEditorScriptingUtilities |
| Data Validation | `Engine/Plugins/Editor/DataValidation/` | true | PORT | 4b | Pre-cook QA | XDataValidation |
| Material Analyzer | `Engine/Plugins/Editor/MaterialAnalyzer/` | true | PORT | 4b | MaterialEditor dep | XMaterialAnalyzer |
| Engine Asset Definitions | `Engine/Plugins/Editor/EngineAssetDefinitions/` | true | PORT | 4a-property | Modern asset registration | XEngineAssetDefinitions |
| Editor Config | `Editor/EditorConfig/` (actually Engine/Source/Editor — see below) | n/a | PORT | 4a-deps | UnrealEd dep (decision #66) | XEditorConfig |
| Property Access | `Engine/Plugins/Runtime/PropertyAccess/` | true | PORT | 4b | UMG MVVM dep (decision #58) | XPropertyAccess |
| Model View View Model | `Engine/Plugins/Runtime/ModelViewViewModel/` | true | PORT | 4b | UMG MVVM | XModelViewViewModel |
| Field Notification | `Runtime/FieldNotification/` (Runtime, not Plugin) | n/a | PORT | 4b | UMG MVVM | XFieldNotification |

## To-Be-Triaged Rows (~50 remaining plugins)

Authored pre-Phase-4a-deps via manager 1–2 week read-only triage:
- Audio: AudioCapture, ResonanceAudio, SoundFields, AudioInsights, AudioWidgets, AudioGameplayVolume, AudioModulation, MetasoundEditor (already in 4c), AudioStreaming
- Editor: AssetSearch, AssetManagerEditor, AssetReferenceFilter, AssetReferenceRestrictions, BlueprintHeaderView, ChangelistReview, ConsoleVariablesEditor, ColorGrading, GameplayInsights, GeometryMode, ImageWidgets, IntroTutorials, LandscapePatch, MaterialBaking, MaterialSnapshotter, MovieScene*, PluginBrowser, ProjectLauncher, RemoteSession, ScriptableEditorWidgets, ScriptPlugin (exclude), SourceCodeAccess providers, StylusInput, SubobjectEditor, Subversion, TextureHistogramTool, ToolPresets, UndoHistoryEditor, Volumetrics, VRMode
- FX: not Niagara — anything else
- Animation: AnimationLocomotionLibrary, AnimationModifierLibrary, AnimationWarping, BlendStack, MotionWarping, PoseSearch, SkeletalReduction, SkeletalMeshModelingTools, PhysicsControl, MLDeformer
- Runtime: GameplayMediaEncoder (Win64), HierarchicalLOD plugins, MediaCompositing, OpenColorIO (Phase 2 named), Optimus, Paper2D (exclude), Synthesizer, UMGRichTextEditor, VirtualHeightfieldMesh, WaveformEditor
- Online: OnlineFramework, OnlineBase, OnlineServices (defer 7+ alongside replication)

## Manager Action Items

- Spend 1–2 weeks pre-Phase-4a-deps walking every `.uplugin` in `C:/Users/benne/Documents/GitHub/UnrealEngine/Engine/Plugins/` and filling out the table above.
- Confirm each disposition with rationale.
- Update the `.uplugin` `EnabledByDefault` flag in the XPact source tree to match for every DISABLE/DEFER row.
- Subagents reading this file as part of Phase 4a-deps brief must NOT modify rows; only the manager updates.
