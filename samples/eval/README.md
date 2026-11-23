# Evaluation labels

The `expectedItems` in each JSON file are frozen ground truth for comparing
extractors. Do not edit labels to improve a score. A label may be changed only
when the transcript or annotation is independently reviewed and the ground
truth correction is documented. Every expected quote must occur verbatim in
its paired synthetic transcript; the backend test suite enforces this rule.

The transcripts are synthetic and are safe for evaluation and local model runs.
