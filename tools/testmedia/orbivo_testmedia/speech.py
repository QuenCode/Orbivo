"""German announcements via the Windows speech synthesizer (no extra dependencies)."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import numpy as np

from .wavfile import read_wav_mono16

TTS_SCRIPT = Path(__file__).with_name("tts.ps1")
NO_VOICE_EXIT_CODE = 3


def synthesize(words: dict[str, str], work_dir: Path, sample_rate: int) -> tuple[dict[str, np.ndarray] | None, str]:
    """Returns ({key: mono samples}, voice name), or (None, reason) if no German voice is available."""
    work_dir.mkdir(parents=True, exist_ok=True)
    jobs = [{"text": text, "path": str(work_dir / f"speech_{key}.wav")} for key, text in words.items()]
    job_file = work_dir / "speech_jobs.json"
    job_file.write_text(json.dumps(jobs, ensure_ascii=False), encoding="utf-8")

    result = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(TTS_SCRIPT),
         "-JobFile", str(job_file), "-SampleRate", str(sample_rate)],
        capture_output=True, text=True)
    if result.returncode == NO_VOICE_EXIT_CODE:
        return None, result.stderr.strip()
    if result.returncode != 0:
        raise RuntimeError(f"Speech synthesis failed (exit {result.returncode}): {result.stderr.strip()}")

    signals = {}
    for key, job in zip(words, jobs):
        samples, rate = read_wav_mono16(Path(job["path"]))
        if rate != sample_rate:
            raise RuntimeError(f"Speech for '{key}' has {rate} Hz instead of {sample_rate} Hz")
        signals[key] = samples.astype(np.float64)
    return signals, result.stdout.strip()
