// Native side of StraitDevice.cs on iOS (Unity has no API for UIScreen.scale or the IANA zone).
// The "Device fields" code from the README. Unity enables .mm files for iOS by default; if your
// project already has a copy (e.g. Assets/Plugins/iOS/StraitDevice.mm), keep only one.
#import <UIKit/UIKit.h>
static char *StraitDup(NSString *s) { const char *c = [s UTF8String]; char *r = (char *)malloc(strlen(c) + 1); strcpy(r, c); return r; }
extern "C" {
  float _StraitScreenPortraitWidth() { CGSize s = UIScreen.mainScreen.bounds.size; return (float)MIN(s.width, s.height); }
  float _StraitScreenScale() { return (float)UIScreen.mainScreen.scale; }
  char *_StraitLanguage() { return StraitDup(NSLocale.preferredLanguages.firstObject ?: @"en"); }
  char *_StraitTimezone() { return StraitDup(NSTimeZone.localTimeZone.name); }
}
