// Strait SDK: iPhone clipboard boost (contract B19), native side of IosStraitClipboard.cs.
// - StraitClipboard_DetectProbableWebURL: UIPasteboard detectPatterns (probableWebURL). It never reads the
//   clipboard, so iOS shows NO paste prompt. iOS 15+; older iOS answers 0. The callback runs on the main queue.
// - StraitClipboard_ReadText: UIPasteboard.general.string. This READS the clipboard, so iOS shows its
//   "Allow Paste" prompt. The SDK calls it only when the app set ClipboardBoost and detection said a web URL
//   is probably there.
// Not compiled in this repository's CI (which is .NET only); it is compiled by Xcode in the game's iOS build.
#import <UIKit/UIKit.h>
#include <string.h>
#include <stdlib.h>

typedef void (*StraitDetectCallback)(int requestId, int probable);

extern "C" {

void StraitClipboard_DetectProbableWebURL(int requestId, StraitDetectCallback callback) {
    if (callback == NULL) return;
    if (@available(iOS 15.0, *)) {
        NSSet<UIPasteboardDetectionPattern> *patterns = [NSSet setWithObject:UIPasteboardDetectionPatternProbableWebURL];
        [[UIPasteboard generalPasteboard] detectPatternsForPatterns:patterns
                                                 completionHandler:^(NSSet<UIPasteboardDetectionPattern> *found, NSError *error) {
            int probable = (error == nil && [found containsObject:UIPasteboardDetectionPatternProbableWebURL]) ? 1 : 0;
            dispatch_async(dispatch_get_main_queue(), ^{ callback(requestId, probable); });
        }];
    } else {
        dispatch_async(dispatch_get_main_queue(), ^{ callback(requestId, 0); });
    }
}

// Returns a malloc'd UTF-8 copy (the IL2CPP marshaller frees it), or NULL when there is no text.
const char *StraitClipboard_ReadText(void) {
    NSString *text = [UIPasteboard generalPasteboard].string;
    if (text == nil) return NULL;
    const char *utf8 = [text UTF8String];
    if (utf8 == NULL) return NULL;
    char *copy = (char *)malloc(strlen(utf8) + 1);
    if (copy == NULL) return NULL;
    strcpy(copy, utf8);
    return copy;
}

}
