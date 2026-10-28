"""Builds src/PARScope.Core/Sim/callword-abbreviations.csv: a 4-, 5- and 6-letter form of every tactical
callword in callsigns.csv, for flight-plan callsigns that must fit a length limit (BANSHEE21 -> BNSH21).

Rules, in order of preference:
  * an official military ICAO designator from airlines.csv (REACH -> RCH, CONVOY -> CNV, QUID -> QID);
  * the word as people shorten it so it still reads and sounds the same (BLACKJACK -> BLKJK, BANSHEE -> BNSHE):
    doubled letters and CK collapse, a silent final E goes, inner vowels go right to left, then a sounded final
    vowel, then truncation;
  * otherwise other forms that keep the first letter, favouring consonants, early letters and the word's ending.
Forms are four letters or more; only an official designator is three (RCH). Readability comes before
uniqueness: a form is moved off a clash only to an alternative that reads nearly as well.

Run from the repository root:  python scripts/callword-abbreviations.py
"""
import csv
import itertools
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIM = os.path.join(ROOT, "src", "PARScope.Core", "Sim")
VOWELS = set("AEIOU")


def collapse(w):
    w = w.replace("CK", "K")
    out = w[0]
    for ch in w[1:]:
        if ch != out[-1]:
            out += ch
    return out


def natural(word, n):
    """The form a person would write and still say the same: collapse doubles (BANSHEE -> BANSHE, BLACKJACK ->
    BLAKJAK), drop a silent final E, drop inner vowels right to left (BLKJK, BNSHE, THNDR), then a sounded final
    vowel, then truncate."""
    if len(word) <= n:
        return word
    w = collapse(word)
    if len(w) <= n:
        return w
    chars = list(w)
    # A final E after a consonant is silent (MOOSE, KNIGHT-E); a doubled EE (BANSHEE) or EY/IE is sounded.
    if chars[-1] == "E" and chars[-2] not in VOWELS and not word.endswith("EE") and len(chars) > n:
        chars.pop()
    i = len(chars) - 2
    while len(chars) > n and i >= 1:
        if chars[i] in VOWELS or (chars[i] == "Y" and i > 0):
            del chars[i]
        i -= 1
    if len(chars) > n and (chars[-1] in VOWELS or chars[-1] == "Y"):
        chars.pop()
    return collapse("".join(chars))[:n]


def score(word, form):
    """Lower is better: consonants kept early, the word's last consonant kept, few vowels."""
    s, pos = 0.0, 0
    consonants = [c for c in word[1:] if c not in VOWELS]
    for ch in form[1:]:
        j = word.index(ch, pos + 1)
        s += (j - pos - 1) * 0.3          # letters skipped
        s += 1.5 if ch in VOWELS else 0    # vowels kept
        pos = j
    if consonants and form[-1] != consonants[-1]:
        s += 1                             # loses the word's ending
    return s


def candidates(word, n):
    first = natural(word, n)
    yield first
    body = collapse(word)
    pool = set()
    for rest in itertools.combinations(range(1, len(body)), n - 1):
        pool.add(body[0] + "".join(body[i] for i in rest))
    if len(word) <= 12:
        for rest in itertools.combinations(range(1, len(word)), n - 1):
            pool.add(word[0] + "".join(word[i] for i in rest))
    for f in sorted(pool, key=lambda f: (score(word, f), f)):
        if f != first:
            yield f


def main():
    words = sorted({r["callsign"].strip().upper() for r in csv.DictReader(open(os.path.join(SIM, "callsigns.csv"), encoding="utf-8"))})
    civil, official = set(), {}
    for r in csv.DictReader(open(os.path.join(SIM, "airlines.csv"), encoding="utf-8")):
        if r["military"].strip():
            official[r["telephony"].strip().upper()] = r["code"].strip().upper()
        else:
            civil.add(r["code"].strip().upper())
    callwords = set(words)
    table = {w: {} for w in words}
    # Never shorter than four letters, except an official three-letter designator (RCH, CNV, QID).
    for w, code in official.items():
        if w in table and len(code) == 3 and len(w) > 3:
            table[w][3] = code
    # Readable first, unique second. Shorter words choose first (THUNDER keeps THND over THUNDERBIRD); a word
    # whose natural form is taken moves to an alternative only if it reads nearly as well (same first two
    # letters, a similar score), otherwise the two share it.
    for n in (4, 5, 6):
        used = {w for w in words if len(w) == n}   # a word that fits as is owns its spelling
        for w in sorted((w for w in words if len(w) > n), key=lambda w: (len(w), w)):
            if w in official and len(official[w]) <= n:
                form = official[w]
            else:
                form = natural(w, n)
                if form in used or form in callwords:
                    limit = score(w, form) + 0.8
                    form = next((f for f in candidates(w, n) if f not in used and f not in callwords
                                 and f[:2] == form[:2] and score(w, f) <= limit), form)
            used.add(form)
            table[w][n] = form
    out = os.path.join(SIM, "callword-abbreviations.csv")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("callword,l3,l4,l5,l6\n")
        for w in words:
            if len(w) > 4:
                f.write(",".join([w] + [table[w].get(n, "") for n in (3, 4, 5, 6)]) + "\n")
    print(f"wrote {out}: {sum(1 for w in words if len(w) > 4)} callwords")


if __name__ == "__main__":
    main()
