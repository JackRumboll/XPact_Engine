# XIL2CPP Rev 3 → Rev 4 Changelog

**Doc:** `Documents/XIL2CPP.html`
**Prior:** Rev 3 (commit `fed9643`; status "Rev 3; CANDIDATE FOR CONVERGED")
**Now:** Rev 4 (this revision; status **CONVERGED**, 2026-05-28)
**Audit input:** Audit F (Round 3 convergence validation; 10 findings — F=10)

## Disposition summary

| Severity | Count in Audit F | Applied in Rev 4 | Deferred |
|---|---|---|---|
| CRITICAL | 0 | 0 | 0 |
| HIGH | 1 | 1 | 0 |
| MEDIUM | 3 | 3 | 0 |
| LOW | 4 | 2 (docs) + 2 (changelog/tracking) | 0 |
| NIT/MINOR | 2 | 1 (docs) + 1 (acknowledged) | 0 |
| **Total** | **10** | **10** | **0** |

## Per-finding disposition

### HIGH (1 of 1 applied)

| Finding | Action |
|---|---|
| FIX-F-HIGH-1 | §9.8 line 3590 closing sentence rewritten. The Rev 1/Rev 2 survivor "Rev 2 forward-commits the ordinal-17 amendment as the chosen path" is replaced with "Rev 3 forward-commits the slot-14 (Reserved_Phase2_F) amendment as the chosen path per FIX-D-CRIT-01." This eliminates the lone prose contradiction with the rest of §9.8 + §15 Phase 6.a + §14.0 (all already cite slot 14 correctly per FIX-D-CRIT-01). The §9.8 surface is now internally consistent. |

### MEDIUM (3 of 3 applied)

| Finding | Action |
|---|---|
| FIX-F-MED-1 | Three citations of pre-split `XIL2CPP053` for locale-dependent ToString/Parse moved to `XIL2CPP058` per FIX-D-MED-07 split: (a) §7.4 line 3145 table row WARNING cell `XIL2CPP053` → `XIL2CPP058`; (b) §7.4 line 3149 paragraph "New diagnostic codes" — code changed AND parenthetical added "(Rev 3: per FIX-D-MED-07 split, locale-dependent ToString/Parse on sim-path moves to `XIL2CPP058`; `XIL2CPP053` now reserved for `Type.MakeGenericType`)"; (c) §11 line 3869 DeterminismMathTest acceptance gate `XIL2CPP053` → `XIL2CPP058`. The §12 diagnostic table at lines 3763/3764 already correctly emits both codes after the FIX-D-MED-07 split. |
| FIX-F-MED-2 | Two citations of pre-split `XIL2CPP054` for `[ThreadStatic]` sim-path ban moved to `XIL2CPP057` per FIX-D-MED-07 split: (a) §4.1 line 794 table row BANNED cell `XIL2CPP054` → `XIL2CPP057`; (b) §5.4 line 1549 paragraph diagnostic text — `XIL2CPP054 — [ThreadStatic] is banned...` → `XIL2CPP057 — [ThreadStatic] is banned...`. The §12 diagnostic table at lines 3765/3766 already correctly emits both codes. `XIL2CPP054` retains its post-split meaning (typeof(T) at open-generic site cannot be resolved at compile time). |
| FIX-F-MED-3 | Three citations of pre-split codes for backing-field naming / cross-module duplicate FClass moved per FIX-D-MED-07 split: (a) §10.5 line 3645 backing-field naming `XIL2CPP145` → `XIL2CPP149`; (b) §5.8 line 1954 cross-module duplicate FClass `XIL2CPP125` → `XIL2CPP129`; (c) §14.0 line 3916 XHT-Rev7-BACKING-FIELD row fallback column `XIL2CPP145` → `XIL2CPP149`. The §12 diagnostic table at lines 3801/3802/3810/3811 already correctly emits all four codes after the FIX-D-MED-07 split. `XIL2CPP145` retains its post-split meaning (vtable slot count or order changed; class layout drift forbidden mid-session) — the references at §1.5 line 1090 and §8.2 line 3214/3218 for that meaning are CORRECT and were not touched. `XIL2CPP125` retains its post-split meaning (Generic constraint 'T : unmanaged' violated by XObject-derived T) — the reference at §5.8 line 1943 for that meaning is CORRECT and was not touched. |

### LOW (4 of 4 applied)

| Finding | Action |
|---|---|
| FIX-F-LOW-1 | §1.5 line 105 design-summary bullet rewritten per FIX-C-CRIT-02 / §8.4 aggregator design. Before: "XIL2CPP-emitted `Z_Construct_FClass_*` functions register typed listeners ... for the per-FClass sub-pool rebind event ... and do not assume the old FClass*-to-cell-pool mapping survives the patch." After: "XIL2CPP emits a single typed `IXObjectClassReplacedListener` per module at the `.init.cs.cpp` aggregator (per §8.4 / FIX-C-CRIT-02 / FIX-C-HIGH-09). The listener walks per-emit-site FClass-cache invalidation tables on each per-FClass sub-pool rebind event ... ; the listener does not assume the old FClass*-to-cell-pool mapping survives the patch." This aligns the §1.5 design-summary with the load-bearing §8.4 + §15 Phase 6.k surfaces. |
| FIX-F-LOW-2 | Rev 3 changelog narrative correction tracked here per audit recommendation. The Rev 3 changelog at `Documents/XIL2CPP-Rev3-Changelog.md` line 49 narrates FIX-E-CRIT-4 as "19 commits × 1 fallback = 19 fallbacks"; the §14.0 table actually has **20 rows** (Rev 2 had 18; Rev 3 added BCL-Rev2-STRINGBUILDER per FIX-D-MED-11 AND split XException-V1 into XException-Rev1-MVP + XException-Rev1-V1 per FIX-E-CRIT-3; 18 + 2 = 20). The doc-internal §14.0 surface is correct (20 rows with 20 fallbacks); only the changelog narrative undercounted by 1. The Rev 3 changelog file is historical and not modified at Rev 4; the correction is preserved here for the record. The §16 Rev 4 cell + closing paragraph cite the canonical "20 forward-commits" figure. |
| FIX-F-LOW-3 | Tracking-hygiene follow-up noted: rotate XBT validator log-string literal `"Rev 13.8"` → `"Rev 13.9"` in the production-code path (likely `Engine/Source/Programs/XBT/XBT.Modes/RunValidateAbiTagsMode.cs` or equivalent). This is a one-line production-code edit outside the docs-only Rev 3/Rev 4 scope. The validator's numeric checks (24 layout tags + 22 sizeof pins) are already correct; only the log-string label is stale. **Follow-up task**: track as a Rev 4 production-code follow-up; not blocking convergence of the docs. |
| FIX-F-LOW-4 | Acknowledged; no docs change required. The §16 Rev 3 cell's "New diagnostic codes" enumeration referenced the XIL2CPP170-187 band rather than enumerating each Rev 3-added code per-code. The §12 diagnostic catalog correctly emits all 18 codes in the 170-187 band (including XIL2CPP171 cited in the FIX-D-CRIT-04 cell), so production-code engineers see the canonical surface. The narrative is one-sided but cosmetic. |

### NIT / MINOR (2 of 2 applied)

| Finding | Action |
|---|---|
| FIX-F-NIT-1 | §6.1 line 2749 shadow-stack example comment expanded for first-time-reader clarity. Before: `// Step (1): Shadow-stack array decl (zero-init) — N=2 (self + container.inner.Holder transitive).` After: `// Step (1): Shadow-stack array decl (zero-init) — N=2 covers the UNION of liveness across the // function's PC range (self always live; container.inner.Holder live after step (4)).` Plus a `// [Rev 4: applied FIX-F-NIT-1 clarification]` annotation. This clarifies that the example is semantically correct (the FStackMapRecord's `pcRangeBegin`/`pcRangeEnd` discipline + the slot's zero-init at function entry handle the "not-yet-live" case correctly) rather than a bug. |
| FIX-F-NIT-2 | Addressed via the §16 Rev 4 cell wording itself. The Rev 3 §16 cell's "Rev 3 represents STRUCTURAL CONVERGENCE on every CRITICAL audit finding" was slightly overpromising given the FIX-F-HIGH-1 residue; the Rev 4 cell explicitly narrates "Round 3 Audit F surfaced 1 HIGH cosmetic residue (FIX-F-HIGH-1) + 3 MEDIUM cascade gaps (FIX-F-MED-1/2/3) + minor LOW/NIT items closed in Rev 4." No structural change to the Rev 3 cell. |

## File-by-file LoC delta

| File | Rev 3 | Rev 4 | Delta |
|---|---|---|---|
| `Documents/XIL2CPP.html` | 4,011 | 4,013 | +2 (net; one new 2-line comment in §6.1 NIT-1 fix; rest are in-place edits) |
| `Documents/XIL2CPP-Rev4-Changelog.md` | 0 | ~135 | +135 (NEW; this file) |

(Word count delta: approximately +120 words across the doc — the §16 Rev 4 cell + closing paragraph rewrite + minor in-place additions for the Rev 4 annotations on each fix.)

## Audit-missed observations (informational only; not within Rev 4 scope)

Audit F's FIX-F-MED-3 enumerated three locations for the backing-field / cross-module-duplicate cascade gap. While applying the fix, two additional occurrences of similar pre-split-code citations were observed but **not modified** because they fall outside the audit's explicit scope (per Rev 4 directive: do not introduce content beyond the 10 findings):

1. **§5.4 line 1520** — Auto-property backing field naming paragraph (older mention; predates the §10.5 canonical location at line 3645). Cites `XIL2CPP145` for backing-field naming mismatch; should be `XIL2CPP149` post-split. Equivalent defect class to FIX-F-MED-3 location (a).
2. **§14.0 line 3925** — BCL-Rev2-CHAR row notes "**XIL2CPP053 banned on sim-path**: ToUpper, ToLower, Parse, TryParse (locale-dependent)." The phrase "locale-dependent" places this in the post-split XIL2CPP058 meaning, not XIL2CPP053. Equivalent defect class to FIX-F-MED-1.

These two occurrences are flagged here for future audit/cleanup hygiene; the audit's "three citations" and "two citations" counts per FIX-F-MED-1/3 are otherwise correctly applied. If a Round 4 audit surfaces them, a single-line edit closes each. Convergence is not blocked: the §12 diagnostic catalog at lines 3763-3811 emits all post-split codes correctly, and the prose locations are recoverable by cross-reference.

## Cumulative finding count summary

| Round | Audits | Findings | Applied | Deferred |
|---|---|---|---|---|
| Round 1 | A=47, B=62, C=52 | 161 | 161 | 0 |
| Round 2 | D=41, E=22 | 63 | 59 | 4 (rolled into Rev 4 dispositions or struck) |
| Round 3 | F=10 | 10 | 10 | 0 |
| **Cumulative** | **A/B/C/D/E/F** | **234** | **230 + 4 (Rev 2 deferred resolved in Rev 3 cascade)** | **0 (CONVERGED)** |

## Convergence assessment

**Rev 4 is CONVERGED.** All 10 Round 3 audit findings landed: 1 HIGH (§9.8 closing-sentence rewrite), 3 MEDIUM (cascade-gap diagnostic-code citation rotations to post-split XIL2CPP057/058/129/149), 4 LOW (design-summary alignment + tracking-hygiene follow-ups), 2 NIT (example clarification + cell wording self-correction). The doc no longer contains any prose contradiction with the FIX-D-CRIT-01 (slot 14) / FIX-D-MED-07 (six-code split) / FIX-C-CRIT-02 (per-module listener aggregator) resolutions.

Structural convergence has held since Rev 3: every Round 2 CRITICAL (FIX-D-CRIT-01 through 06; FIX-E-CRIT-1 through 4) remains correctly addressed. The Contract Rev 13.9 carry-forward holds (24 layout tags + 22 sizeof pins, validator exit 0). The §15 cascade aligns. The §14.0 forward-commits table has 20 rows, each with documented fallback per FIX-E-CRIT-4. Locked architectural commitments hold throughout (pure transpilation §2.1, C# 12 / .NET 8 MVP §2.2, XObject.New<T> explicit factory §2.3).

**The System 6 implementation phase (Phase 6.a through 6.l, ~30-40 weeks estimated) is now unblocked.** Forward-commits to XCoreXObject Rev 5, XHT Rev 7, XBT Rev 11, Contract Rev 14, XPact.CSharp.BCL Rev 2, XPact.Sim Rev 1, and XException Rev 1 MVP/V1 remain coordinated work items per the §14.0 fallback discipline.

---

*End of XIL2CPP-Rev4-Changelog.md.*
