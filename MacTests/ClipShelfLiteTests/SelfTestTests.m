#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

extern int32_t ClipShelfRunSharedSelfTests(void);

__attribute__((constructor))
static void runClipShelfSelfTests(void) {
    int32_t result = ClipShelfRunSharedSelfTests();
    if (result != 0) {
        fputs("ClipShelf shared self-tests failed.\n", stderr);
        abort();
    }
}
