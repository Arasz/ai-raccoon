# No hand-rolled crypto or security orchestration

Never implement security/cryptographic orchestration yourself — key derivation, token signing, session/cookie protection, encryption-at-rest schemes. Delegate to an audited, platform-provided library rather than composing audited primitives into your own protocol, even when the primitives themselves are sound.
