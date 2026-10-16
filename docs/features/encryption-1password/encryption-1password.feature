# Encryption key source — 1Password via the op CLI (behavioral contract, ADR-0132)

@bdd @1password
Feature: 1Password as a bank key source

    As a user who keeps SSH keys in 1Password
    I want the DB encryption key to come from a 1Password SSH Key item through the op CLI
    So that the bank key lives in my vault and never in a client config

    # Decisions (ADR-0132):
    # - Source name "1password"; the item is an SSH Key item holding an unencrypted ed25519 key,
    #   derived exactly like the Bitwarden key (HKDF-SHA-256, ADR-0012). There is no legacy key.
    # - The fetch is `op read <reference>`; the reference (op://vault/item/field?ssh-format=openssh)
    #   is stored verbatim in the secret id slot. No project id exists for this source.
    # - Authentication is inherited: OP_SERVICE_ACCOUNT_TOKEN from the environment, or the 1Password
    #   desktop app. The command takes no token option, because argv is readable through ps.
    # - A reference that does not start with op:// is refused before any fetch.

    Rule: encryption 1password validates the op CLI before configuring
        Scenario: Config fails with install guidance when op is missing
            Given op is not installed
            When the user runs encryption 1password
            Then the command errors with a message to install op
            And the 1password setup changed nothing

    Rule: encryption 1password collects an op:// secret reference
        Scenario: Interactive config persists the reference
            Given op is installed
            When the user runs encryption 1password with reference op://vault/item/private key?ssh-format=openssh
            Then the encryption source is set to 1password
            And the reference is persisted as the secret id with no project id
            And op was asked to read the reference

        Scenario: A reference that is not an op:// reference is refused
            Given op is installed
            When the user runs encryption 1password with reference https://example.com/not-a-reference
            Then the command is refused as an invalid reference
            And op was not asked to read anything
            And the 1password setup changed nothing

    Rule: The configured source is what opens the bank
        Scenario: The bank opens with the key op returns
            Given the encryption source is 1password
            And the service account token ops_test_token_0123 is in the environment
            When the server opens the bank through op
            Then the bank opens with the key op returned
            And op received the token through its environment and not its arguments

        Scenario: Show prints the source and reference
            Given the encryption source is 1password
            When the user shows the encryption source
            Then it prints 1password with the reference
