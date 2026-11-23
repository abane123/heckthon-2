# Transcript extraction evaluation

Provider: **Stub**
Model: **Built-in rules**
Runs per transcript: **3**

| Transcript | Language | Extracted | Expected | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Avg (s) | Min (s) | Max (s) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 01-release-readiness | English | 2 | 2 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.03 | 0.00 | 0.09 |
| 02-interrupted-handoff | English | 2 | 2 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 | 0.00 | 0.00 |
| 03-first-person | English | 2 | 2 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 | 0.00 | 0.00 |
| 04-ambiguous-phrases | English | 2 | 2 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 | 0.00 | 0.00 |
| 05-last-minute-changes | English | 3 | 3 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 | 0.00 | 0.00 |
| 06-hinglish | Hinglish | 1 | 2 | 100.0% | 50.0% | 0.0% | 0.0% | 0 | 0.00 | 0.00 | 0.00 |
| 07-devanagari | Hindi (Devanagari) | 0 | 2 | N/A | 0.0% | N/A | N/A | 0 | 0.00 | 0.00 | 0.00 |
| 08-mixed-correction-reassignment | Mixed English/Hinglish | 1 | 1 | 0.0% | 0.0% | N/A | N/A | 0 | 0.00 | 0.00 | 0.00 |
| **Total** |  | **13** | **16** | **92.3%** | **75.0%** | **91.7%** | **91.7%** | **0** | **0.00** | **0.00** | **0.09** |

## Per-language breakdown

| Language | Extracted | Expected | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Avg (s) | Min (s) | Max (s) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| English | 11 | 11 | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.01 | 0.00 | 0.09 |
| Hindi (Devanagari) | 0 | 2 | N/A | 0.0% | N/A | N/A | 0 | 0.00 | 0.00 | 0.00 |
| Hinglish | 1 | 2 | 100.0% | 50.0% | 0.0% | 0.0% | 0 | 0.00 | 0.00 | 0.00 |
| Mixed English/Hinglish | 1 | 1 | 0.0% | 0.0% | N/A | N/A | 0 | 0.00 | 0.00 | 0.00 |

Precision/recall use exact normalized source-quote matches; field accuracies are measured over those matches.