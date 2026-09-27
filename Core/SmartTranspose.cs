namespace GoMidi.Core;

/// <summary>Automatic transposition per track, plus the all-tracks fallback.</summary>
public sealed class TransposePlan
{
    public int[] TrackShift { get; init; } = Array.Empty<int>();

    /// <summary>Shift used by channels set to "全部音轨".</summary>
    public int GlobalShift { get; init; }

    public int ForTrack(int trackIndex) =>
        trackIndex >= 0 && trackIndex < TrackShift.Length ? TrackShift[trackIndex] : GlobalShift;
}

/// <summary>
/// Chooses the semitone offset that best fits a track onto the playable key
/// range: a per-track pitch histogram, a skew-adaptive boost that protects the
/// melody's highest notes, then an octave search.
///
/// The search scores candidates by <em>how many notes actually become
/// playable</em> first, and only then by how well they sit in the middle of the
/// range. Ranking purely on centring — as the original did — can pick an offset
/// that leaves every note out of range, which is indistinguishable from a
/// broken player.
/// </summary>
public static class SmartTranspose
{
    /// <summary>Base multiplier applied to the top of the histogram.</summary>
    private const double HighNoteBaseBoost = 1.2;

    /// <summary>Falloff of the boost per semitone below the highest note.</summary>
    private const double HighNoteStepDecay = 0.1;

    /// <summary>Minimum number of semitones the boost covers, however narrow the range.</summary>
    private const int MinBoostDepth = 4;

    /// <summary>Boost depth as a percentage of the playable range.</summary>
    private const int BoostDepthPercent = 15;

    /// <summary>Candidates are whole octaves within ±4.</summary>
    private const int OctaveRange = 4;

    private const int SemitonesPerOctave = 12;

    /// <summary>
    /// Weight that makes "more playable notes" dominate "nicer placement". The
    /// placement term alone can never outvote a single extra playable note.
    /// </summary>
    private const double PlayabilityWeight = 1000.0;

    /// <summary>Full 0–127 range means "no smart transposition".</summary>
    public static TransposePlan Build(
        IReadOnlyList<RawNote> notes,
        int trackCount,
        int minPitch,
        int maxPitch,
        Func<int, bool> hasBinding)
    {
        trackCount = Math.Max(1, trackCount);
        var trackShift = new int[trackCount];

        if (notes.Count == 0 || (minPitch <= 0 && maxPitch >= 127))
        {
            return new TransposePlan { TrackShift = trackShift, GlobalShift = 0 };
        }

        var perTrack = new double[trackCount][];
        for (int t = 0; t < trackCount; t++)
        {
            perTrack[t] = new double[128];
        }

        // The global histogram prefers melodic material and drops percussion and
        // the bass/violin families, which would otherwise drag the centre down.
        var melodic = new double[128];
        var everything = new double[128];

        foreach (RawNote note in notes)
        {
            if (note.Pitch is < 0 or > 127)
            {
                continue;
            }

            // Longer and louder notes carry more of the melody.
            double weight = Math.Sqrt(Math.Max(0.0, note.Duration)) * note.Velocity;

            if (note.TrackIndex >= 0 && note.TrackIndex < trackCount)
            {
                perTrack[note.TrackIndex][note.Pitch] += weight;
            }

            everything[note.Pitch] += weight;

            bool isPercussion = note.Channel == 9;
            bool isBassOrStrings = note.Program == 43 || note.Program is >= 33 and <= 40;
            if (!isPercussion && !isBassOrStrings)
            {
                melodic[note.Pitch] += weight;
            }
        }

        // A drum-only arrangement, or one written entirely for the excluded
        // programs, leaves the melodic histogram empty. Falling back to every
        // note keeps the material placeable instead of transposing by zero and
        // dropping the lot.
        double[] global = melodic.All(w => w <= 0) ? everything : melodic;

        for (int t = 0; t < trackCount; t++)
        {
            if (perTrack[t].All(w => w <= 0))
            {
                continue;
            }

            ApplyHighNoteProtection(perTrack[t], minPitch, maxPitch);
            trackShift[t] = SelectBestOctave(perTrack[t], minPitch, maxPitch, hasBinding);
        }

        int globalShift = 0;
        if (global.Any(w => w > 0))
        {
            ApplyHighNoteProtection(global, minPitch, maxPitch);
            globalShift = SelectBestOctave(global, minPitch, maxPitch, hasBinding);
        }

        return new TransposePlan { TrackShift = trackShift, GlobalShift = globalShift };
    }

    /// <summary>
    /// Multiplies the histogram's top bins by a skew-derived boost so a
    /// transposition that fits the bulk of the material but pushes the melody
    /// out of range loses. A track already sitting high gets a stronger boost.
    /// </summary>
    private static void ApplyHighNoteProtection(double[] histogram, int minPitch, int maxPitch)
    {
        double sumWeight = 0;
        double weightedSum = 0;
        int highest = -1;

        for (int pitch = 0; pitch < 128; pitch++)
        {
            double w = histogram[pitch];
            if (w <= 0)
            {
                continue;
            }

            sumWeight += w;
            weightedSum += pitch * w;
            highest = pitch;
        }

        if (highest < 0 || sumWeight < 1e-10)
        {
            return;
        }

        double mean = weightedSum / sumWeight;
        double variance = 0;
        double thirdMoment = 0;
        for (int pitch = 0; pitch < 128; pitch++)
        {
            double w = histogram[pitch];
            if (w <= 0)
            {
                continue;
            }

            double d = pitch - mean;
            variance += w * d * d;
            thirdMoment += w * d * d * d;
        }

        variance /= sumWeight;
        thirdMoment /= sumWeight;

        double skew = variance >= 1e-10 ? thirdMoment / Math.Pow(variance, 1.5) : 0;

        double skewAdjust = 1.0 + Math.Clamp(-skew * 0.12, -0.3, 0.5);
        double pitchRange = maxPitch - minPitch;
        int boostDepth = Math.Max(MinBoostDepth, (int)(pitchRange * BoostDepthPercent / 100.0 * skewAdjust));
        double baseBoost = 1.0 + (HighNoteBaseBoost - 1.0) * skewAdjust;

        for (int distance = 0; distance <= boostDepth; distance++)
        {
            int pitch = highest - distance;
            if (pitch is < 0 or > 127)
            {
                break;
            }

            double boost = baseBoost - HighNoteStepDecay * distance;
            if (boost > 1.0)
            {
                histogram[pitch] *= boost;
            }
        }
    }

    /// <summary>
    /// Scores every whole-octave shift from −4 to +4. Playability dominates, so
    /// the winner is the offset that gets the most notes onto real keys; the
    /// Gaussian term only decides between offsets that are equally playable.
    /// </summary>
    private static int SelectBestOctave(double[] histogram, int minPitch, int maxPitch, Func<int, bool> hasBinding)
    {
        double center = (minPitch + maxPitch) / 2.0;
        double halfRange = Math.Max((maxPitch - minPitch) / 2.0, 1.0);
        double sigma = halfRange * 0.4;

        int bestShift = 0;
        double bestScore = double.NegativeInfinity;
        double bestPlayable = 0;

        for (int octave = -OctaveRange; octave <= OctaveRange; octave++)
        {
            int shift = octave * SemitonesPerOctave;
            double playable = 0;
            double placement = 0;

            int low = Math.Max(0, minPitch - shift);
            int high = Math.Min(127, maxPitch - shift);
            for (int pitch = low; pitch <= high; pitch++)
            {
                double w = histogram[pitch];
                if (w <= 0)
                {
                    continue;
                }

                int shifted = pitch + shift;
                if (!hasBinding(shifted))
                {
                    continue; // outside the range, or no key bound to it
                }

                playable += w;

                double d = (shifted - center) / sigma;
                placement += w * Math.Exp(-0.5 * d * d);
            }

            double score = playable * PlayabilityWeight + placement;

            // Strictly greater keeps the earliest (smallest |octave|) candidate on ties.
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                bestShift = shift;
                bestPlayable = playable;
            }
        }

        // If no offset gets a single note onto a key, the keymap is the problem,
        // not the pitch. Reporting shift 0 keeps the diagnosis honest instead of
        // blaming a transposition that could never have helped.
        return bestPlayable > 0 ? bestShift : 0;
    }
}
