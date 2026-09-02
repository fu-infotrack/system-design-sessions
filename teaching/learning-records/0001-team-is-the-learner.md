# The learner is the team, not the user

The user is teaching a 25-minute locking session and asked for lessons aimed at
their team, not at themselves. They already hold this material at depth — they
built the demos, and they corrected the source material twice during its
development (that `pg_advisory_lock` has legitimate process-lifetime uses, and
that Kafka's partition guarantee genuinely does remove the need for a lock).

**Implications:** pitch every lesson at a mixed-seniority .NET team with no
formal grounding in locking. Never pitch at the user. Their corrections are a
reliable signal for what the material gets wrong, not for what they need taught.
