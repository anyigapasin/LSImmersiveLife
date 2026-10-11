using System;
using GTA;
using GTA.Math;
using GTA.Native;

/// <summary>
/// Runtime checks for authored animation clips. The official documents name
/// intended clips; this helper checks what the running game actually exposes
/// before any gameplay owner asks GTA to play one.
/// </summary>
internal static class LSImmersiveDictionaryAnimation
{
    internal enum ArrestTargetPosture
    {
        Invalid,
        Dead,
        Cuffed,
        Grounded,
        Kneeling,
        Standing
    }

    internal static ArrestTargetPosture ClassifyArrestTarget(Ped ped)
    {
        if (ped == null || !ped.Exists())
            return ArrestTargetPosture.Invalid;
        if (ped.IsDead || ped.Health <= 0)
            return ArrestTargetPosture.Dead;

        try
        {
            if (Function.Call<bool>(Hash.IS_PED_CUFFED, ped))
                return ArrestTargetPosture.Cuffed;
        }
        catch { }

        try
        {
            if (Function.Call<bool>(Hash.IS_PED_RAGDOLL, ped)
                || Function.Call<bool>(Hash.IS_PED_IN_WRITHE, ped))
                return ArrestTargetPosture.Grounded;
        }
        catch { }

        if (IsPlaying(ped, "random@arrests", "kneeling_arrest_idle")
            || IsPlaying(ped, "random@arrests", "kneeling_arrest_get_up"))
            return ArrestTargetPosture.Kneeling;

        return ArrestTargetPosture.Standing;
    }

    internal static bool TryPlaySynchronizedPair(
        string dictionary,
        Ped first,
        string firstClip,
        Ped second,
        string secondClip,
        float blendInSpeed,
        float blendOutSpeed,
        int durationMilliseconds,
        float playbackRate,
        out int sceneId,
        out float longestClipDuration,
        out Vector3 sceneOrigin,
        out float sceneHeading)
    {
        sceneId = -1;
        longestClipDuration = 0f;
        sceneOrigin = Vector3.Zero;
        sceneHeading = 0f;
        if (first == null || !first.Exists() || first.IsDead
            || second == null || !second.Exists() || second.IsDead
            || first.Handle == second.Handle
            || first.IsInVehicle() || second.IsInVehicle()
            || float.IsNaN(playbackRate) || float.IsInfinity(playbackRate)
            || playbackRate <= 0f || playbackRate > 1f
            || string.IsNullOrWhiteSpace(dictionary)
            || string.IsNullOrWhiteSpace(firstClip)
            || string.IsNullOrWhiteSpace(secondClip))
            return false;

        float firstDuration;
        float secondDuration;
        if (!TryGetClipDuration(dictionary, firstClip, out firstDuration)
            || !TryGetClipDuration(dictionary, secondClip, out secondDuration))
            return false;

        Vector3 firstPosition = first.Position;
        Vector3 secondPosition = second.Position;
        float distance = firstPosition.DistanceTo(secondPosition);
        if (distance > 2.75f || Math.Abs(firstPosition.Z - secondPosition.Z) > 1.25f)
            return false;

        try
        {
            // The paired arrest clips use their authored actor offsets from
            // the synchronized-scene root. Root at the officer and align the
            // scene rotation to that root actor. Leave both Ped headings to
            // the synchronized task; forcing identical entity headings here
            // made the authored cop/crook roles look misaligned.
            sceneHeading = first.Heading;
            sceneOrigin = firstPosition;
            first.Task.ClearAll();
            second.Task.ClearAll();

            sceneId = Function.Call<int>(
                Hash.CREATE_SYNCHRONIZED_SCENE,
                sceneOrigin.X, sceneOrigin.Y, sceneOrigin.Z,
                0f, 0f, sceneHeading, 2);
            if (sceneId < 0)
                return false;

            // The two authored roles share one scene phase. Flag 4 keeps GTA
            // from replacing either half before the physical exchange finishes.
            Function.Call(Hash.TASK_SYNCHRONIZED_SCENE,
                first, sceneId, dictionary, firstClip,
                blendInSpeed, blendOutSpeed, durationMilliseconds,
                4, playbackRate, 0);
            Function.Call(Hash.TASK_SYNCHRONIZED_SCENE,
                second, sceneId, dictionary, secondClip,
                blendInSpeed, blendOutSpeed, durationMilliseconds,
                4, playbackRate, 0);

            longestClipDuration = Math.Max(firstDuration, secondDuration);
            return true;
        }
        catch
        {
            try { if (first != null && first.Exists()) first.Task.ClearAll(); } catch { }
            try { if (second != null && second.Exists()) second.Task.ClearAll(); } catch { }
            sceneId = -1;
            longestClipDuration = 0f;
            sceneOrigin = Vector3.Zero;
            sceneHeading = 0f;
            return false;
        }
    }

    internal static bool TryPlaySynchronizedArrest(
        Ped officer,
        Ped suspect,
        out int sceneId,
        out float longestClipDuration)
    {
        Vector3 sceneOrigin;
        float sceneHeading;
        return TryPlaySynchronizedArrest(
            officer, suspect, out sceneId, out longestClipDuration,
            out sceneOrigin, out sceneHeading);
    }

    internal static bool TryPlaySynchronizedArrest(
        Ped officer,
        Ped suspect,
        out int sceneId,
        out float longestClipDuration,
        out Vector3 sceneOrigin,
        out float sceneHeading)
    {
        return TryPlaySynchronizedPair(
            "mp_arrest_paired",
            officer,
            "cop_p2_back_right",
            suspect,
            "crook_p2_back_right",
            4.0f,
            -4.0f,
            -1,
            1.0f,
            out sceneId,
            out longestClipDuration,
            out sceneOrigin,
            out sceneHeading);
    }

    internal static bool TryPlaySynchronizedGroundArrest(
        Ped officer,
        Ped suspect,
        out int sceneId,
        out float longestClipDuration)
    {
        Vector3 sceneOrigin;
        float sceneHeading;
        return TryPlaySynchronizedGroundArrest(
            officer, suspect, out sceneId, out longestClipDuration,
            out sceneOrigin, out sceneHeading);
    }

    internal static bool TryPlaySynchronizedGroundArrest(
        Ped officer,
        Ped suspect,
        out int sceneId,
        out float longestClipDuration,
        out Vector3 sceneOrigin,
        out float sceneHeading)
    {
        return TryPlaySynchronizedPair(
            "mp_arresting",
            officer,
            "arrest_on_floor_front_right_a",
            suspect,
            "arrest_on_floor_front_right_b",
            4.0f,
            -4.0f,
            -1,
            1.0f,
            out sceneId,
            out longestClipDuration,
            out sceneOrigin,
            out sceneHeading);
    }

    internal static bool TryGetSynchronizedScenePhase(int sceneId, out float phase)
    {
        phase = -1f;
        if (sceneId < 0)
            return false;
        try
        {
            float value = Function.Call<float>(Hash.GET_SYNCHRONIZED_SCENE_PHASE, sceneId);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                return false;
            phase = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void RequestDictionary(string dictionary)
    {
        if (string.IsNullOrWhiteSpace(dictionary))
            return;
        try
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
        }
        catch
        {
            // Resource streaming is optional presentation; the owning gameplay
            // state machine decides how to proceed if the dictionary is absent.
        }
    }

    internal static bool TryGetClipDuration(
        string dictionary,
        string clip,
        out float duration)
    {
        duration = 0f;
        if (string.IsNullOrWhiteSpace(dictionary) || string.IsNullOrWhiteSpace(clip))
            return false;

        try
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                return false;

            float loadedDuration = Function.Call<float>(
                Hash.GET_ANIM_DURATION, dictionary, clip);
            if (float.IsNaN(loadedDuration) || float.IsInfinity(loadedDuration)
                || loadedDuration <= 0f)
                return false;

            duration = loadedDuration;
            return true;
        }
        catch
        {
            // Animation lookup is optional presentation. A native/API failure
            // must not stop the owning gameplay state machine.
            return false;
        }
    }

    internal static bool IsDictionaryLoaded(string dictionary)
    {
        if (string.IsNullOrWhiteSpace(dictionary))
            return false;

        try
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
            return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary);
        }
        catch
        {
            return false;
        }
    }
    internal static bool TryPlay(
        Ped ped,
        string dictionary,
        string clip,
        float blendInSpeed,
        float blendOutSpeed,
        int durationMilliseconds,
        int flags,
        float playbackRate,
        out float clipDuration)
    {
        clipDuration = 0f;
        // TASK_PLAY_ANIM uses zero as a frozen playback rate. Reject invalid
        // rates here so an optional pose cannot silently look like a static
        // or lifeless task while still being reported as playing.
        if (float.IsNaN(playbackRate) || float.IsInfinity(playbackRate)
            || playbackRate <= 0f || playbackRate > 1f)
            return false;

        try
        {
            if (ped == null || !ped.Exists()
                || !TryGetClipDuration(dictionary, clip, out clipDuration))
                return false;

            Function.Call(Hash.TASK_PLAY_ANIM,
                ped,
                dictionary,
                clip,
                blendInSpeed,
                blendOutSpeed,
                durationMilliseconds,
                flags,
                playbackRate,
                false,
                false,
                false);
            return true;
        }
        catch
        {
            clipDuration = 0f;
            return false;
        }
    }

    internal static bool IsPlaying(Ped ped, string dictionary, string clip)
    {
        try
        {
            if (ped == null || !ped.Exists()
                || string.IsNullOrWhiteSpace(dictionary)
                || string.IsNullOrWhiteSpace(clip))
                return false;

            return Function.Call<bool>(
                Hash.IS_ENTITY_PLAYING_ANIM, ped, dictionary, clip, 3);
        }
        catch
        {
            return false;
        }
    }

}
