# Tests are designed before they are written, and judged after

Green is the floor, not the evidence: a test list comes out of the acceptance criteria before the first test is written (`design-tests`, each row naming the failure mode it targets and the mutation that proves it real), and a change that adds or alters tests is not done until something other than its author has run `review-tests` and asked whether that suite could have gone red.
