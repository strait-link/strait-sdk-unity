# Security policy

## Reporting a vulnerability

Please report security issues privately to **security@straitlink.in**.
Do not open a public GitHub issue, pull request or discussion for a suspected
vulnerability.

Our contact details and disclosure policy are published at
https://straitlink.in/.well-known/security.txt (policy: https://straitlink.in/security/).

Please include:

- the affected package and version (`strait-sdk-unity`, see CHANGELOG.md),
- steps to reproduce or a proof of concept,
- the impact you observed or expect.

We acknowledge reports as soon as we can, keep you updated while we work on a
fix, and credit you in the release notes if you would like.

## Supported versions

Security fixes are released for the latest published version of this SDK.

## Keys

This SDK only needs your workspace's **publishable** key (`st_pub_live_…` /
`st_pub_test_…`), which is safe to ship in apps and web pages. Never put a
secret key (`st_live_…` / `st_test_…`) in client code. The server SDK
(`strait-sdk-node`) is the only one that takes a secret key, and it belongs on
your backend only. If a secret key leaks, rotate it in the dashboard
(Settings → API keys) right away.
