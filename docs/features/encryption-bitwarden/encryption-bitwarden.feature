# Encryption key sources — env (default) + Bitwarden via bws CLI (behavioral contract)

@bdd
Feature: Encryption key sources

    As a user of the encrypted bank
    I want the DB encryption key to come from AIRACCOON_DB_PASSPHRASE or from Bitwarden Secrets Manager via bws
    So that I can keep the passphrase out of client configs while the zero-setup env path still works

    # Owner decisions (f: 2026-08-05; supersede report F28 where they differ):
    # - Env var STAYS as the default source.
    # - Bitwarden via the bws CLI (installed + token configured by the user): provider
    #   shells out to `bws secret get <secret-id>`; SDK is NOT used.
    # - Secret VALUE = unencrypted ed25519 SSH private key; derived to the SQLCipher raw
    #   key: SHA-256("ai-raccoon-db-key/v1" || seed) -> x'<64hex>' (no KDF; measured).
    # - Offline behavior: refuse to start, loudly.
    # - Rotation in the Bitwarden UI without PRAGMA rekey bricks the bank — config warns.
    # - Raw-key-file option REMOVED; keychain/cloud sources documented but not implemented.
    # - The project/secret defaults are the owner's: project 613165e6-7947-49e0-889b-b49d007c5b85,
    #   secret f1d3c8e5-5391-4aef-8611-b49d007c8702 (IDs are stable; names change).

    Rule: The env variable remains the default key source
        Scenario: The bank opens with AIRACCOON_DB_PASSPHRASE when no encryption source is configured
            Given no encryption source configured
            And AIRACCOON_DB_PASSPHRASE is set
            When the server opens the bank
            Then the bank opens with the env passphrase

    Rule: encryption bitwarden validates the bws CLI before configuring
        Scenario: Config fails with install guidance when bws is missing
            Given bws is not installed
            When the user runs encryption bitwarden
            Then the command errors with a message to install bws and configure a token
            And no encryption source is changed

    Rule: encryption bitwarden collects project id, secret id, and an optional token
        Scenario: Interactive config persists project and secret ids
            Given bws is installed
            When the user runs encryption bitwarden with project 613165e6-7947-49e0-889b-b49d007c5b85 and secret f1d3c8e5-5391-4aef-8611-b49d007c8702
            Then the encryption source is bitwarden
            And the project id and secret id are persisted

        Scenario: The optional -t token is used for the run and never persisted
            Given bws is installed
            When the user runs encryption bitwarden with -t <token>
            Then the source is configured and validated with that token
            And the token is not stored anywhere

        Scenario: Config validates secret reachability
            Given bws is installed
            When the user runs encryption bitwarden with an unreachable secret id
            Then the command errors with the bws failure message
            And no encryption source is changed

    Rule: The secret value is an unencrypted ed25519 SSH private key, derived to the raw SQLCipher key
        Scenario: An ed25519 key derives the raw key deterministically
            Given an unencrypted ed25519 private key with seed <seed>
            When the raw key is derived
            Then it equals SHA-256("ai-raccoon-db-key/v1" || seed) formatted as x'<64hex>'

        Scenario: A passphrase-protected SSH key is rejected
            Given an encrypted ed25519 private key
            When the provider reads the secret
            Then it errors with a message that passphrase-protected keys are unsupported

        Scenario: An RSA private key is rejected
            Given an RSA private key
            When the provider reads the secret
            Then it errors with a message that only ed25519 keys are supported

    Rule: The server refuses to start when the Bitwarden source is unavailable
        Scenario: bws missing at server start fails loudly
            Given the encryption source is bitwarden
            And bws is not installed
            When the server opens the bank
            Then it exits with an actionable error

        Scenario: Network failure at server start fails loudly
            Given the encryption source is bitwarden
            When bws cannot reach Bitwarden
            Then the server exits with an actionable error
            And no cached key is used

    Rule: The bank opens with the derived key from Bitwarden
        Scenario: A bank encrypted with the env passphrase reopens with the derived key after rekey
            Given a bank encrypted with the env passphrase
            When the source is switched to bitwarden and the bank is rekeyed to the derived raw key
            Then the bank opens with the derived key

        Scenario: A wrong key is rejected
            Given the encryption source is bitwarden
            When bws returns a different key than the bank was encrypted with
            Then the server errors with an encryption mismatch

    Rule: The config command warns about the rotation trap
        Scenario: encryption bitwarden prints the rotation warning
            Given bws is installed
            When the user runs encryption bitwarden
            Then the output warns that rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank

    Rule: The encryption source is inspectable and resettable
        Scenario: encryption show prints the current source
            Given the encryption source is bitwarden
            When the user runs encryption show
            Then it prints bitwarden with the secret id

        Scenario: encryption unset returns to the env default
            Given the encryption source is bitwarden
            When the user runs encryption unset
            Then the encryption source is env
