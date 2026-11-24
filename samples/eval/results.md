# Transcript extraction evaluation

Provider: **Stub**

| Transcript | Language | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 01-release-readiness | English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.03 |
| 02-interrupted-handoff | English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 |
| 03-first-person | English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 |
| 04-ambiguous-phrases | English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 |
| 05-last-minute-changes | English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.00 |
| 06-hinglish | Hinglish | 100.0% | 50.0% | 0.0% | 0.0% | 0 | 0.00 |
| 07-devanagari | Hindi (Devanagari) | 0.0% | 0.0% | 0.0% | 0.0% | 0 | 0.00 |
| 08-mixed-correction-reassignment | Mixed English/Hinglish | 0.0% | 0.0% | 0.0% | 0.0% | 0 | 0.00 |
| **Total** | **92.3%** | **75.0%** | **91.7%** | **91.7%** | **0** | **0.00** |

## Per-language breakdown

| Language | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| English | 100.0% | 100.0% | 100.0% | 100.0% | 0 | 0.01 |
| Hindi (Devanagari) | 0.0% | 0.0% | 0.0% | 0.0% | 0 | 0.00 |
| Hinglish | 100.0% | 50.0% | 0.0% | 0.0% | 0 | 0.00 |
| Mixed English/Hinglish | 0.0% | 0.0% | 0.0% | 0.0% | 0 | 0.00 |

Precision/recall use exact normalized source-quote matches; field accuracies are measured over those matches.