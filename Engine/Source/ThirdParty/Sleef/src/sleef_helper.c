/*
 * Copyright Naoki Shibata and contributors 2010 - 2024.
 * Distributed under the Boost Software License, Version 1.0.
 * (See accompanying file ../LICENSE.txt or copy at
 *    http://www.boost.org/LICENSE_1_0.txt.)
 *
 * sleef_helper.c -- shared scalar helpers (Sleef-3.6 src/common/misc.h
 * + src/libm/dd.h excerpt).  Compiled as a separate TU so the bit-
 * pattern helpers above link into ONE .o that both sleef_scalar_sp.c
 * and sleef_scalar_dp.c can call.  The actual bodies are in the .h
 * file (static SLEEF_INLINE) -- this TU exists so the file is part of
 * the linker scan surface, and so future Sleef-3.6 upstream-tarball
 * substitution at Phase 1g has a place to drop the longer
 * common/df.c-style helpers without restructuring the build.
 */

#include "sleef_internal.h"

/* Anchor symbol so the link surface has at least one external from
 * this TU; required for the SleefFMACheck disassembly pass to be sure
 * the file participated in the link.  The symbol is referenced by
 * sleef_scalar_sp.c via xpact_sleef_helper_anchor() at the bottom of
 * each kernel TU so the linker keeps it.
 */
int32_t xpact_sleef_helper_anchor(void) { return 0; }
