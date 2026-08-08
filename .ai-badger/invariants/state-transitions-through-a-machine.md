# Route state transitions through a state machine

Where a domain object has explicit states, make the declared transitions the only way it moves between them, and record what triggered each move. A status field assigned in one place and read in five is a state machine nobody can see, and it becomes unreviewable the first time two writers disagree. Keep a "needs human attention" signal a flag on the entity rather than a state of its own, or every real state acquires a shadow twin and the transition table doubles.
