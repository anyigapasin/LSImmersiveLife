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
        out float longestClipDuration)
    {
        sceneId = -1;
        longestClipDuration = 0f;
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
            float heading = (float)(Math.Atan2(
                secondPosition.X - firstPosition.X,
                secondPosition.Y - firstPosition.Y) * (180.0 / Math.PI));
            if (heading < 0f)
                heading += 360f;

            Vector3 origin = new Vector3(
                (firstPosition.X + secondPosition.X) * 0.5f,
                (firstPosition.Y + secondPosition.Y) * 0.5f,
                (firstPosition.Z + secondPosition.Z) * 0.5f);

            Function.Call(Hash.SET_ENTITY_HEADING, first, heading);
            Function.Call(Hash.SET_ENTITY_HEADING, second, (heading + 180f) % 360f);
            first.Task.ClearAll();
            second.Task.ClearAll();

            sceneId = Function.Call<int>(
                Hash.CREATE_SYNCHRONIZED_SCENE,
                origin.X, origin.Y, origin.Z,
                0f, 0f, heading, 2);
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
            return false;
        }
    }

    internal static bool TryPlaySynchronizedArrest(
        Ped officer,
        Ped suspect,
        out int sceneId,
        out float longestClipDuration)
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
            out longestClipDuration);
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
