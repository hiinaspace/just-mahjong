
using UdonSharp;
using UnityEngine;
using VRC.Udon;

/// <summary>
/// Hashed Wheel timer for scheduling UdonBehaviour.SendCustomEvents.
/// slot buckets are kept as unordered ArrayLists.
/// Both one-shot Delay and forever-repeating Repeat are supported.
/// TODO support cancellation
/// </summary>
public class TimerWheel : UdonSharpBehaviour
{
    bool initialized = false;

    private const int WHEEL_SIZE = 256;
    private const int INITIAL_BUCKET_SIZE = 10;
    // granularity of delays. Note that Update() timing supercedes this, so
    // min granularity is likely 144hz (index) = 0.007f anyway.
    private const float WHEEL_RESOLUTION = 0.01f;
    // XXX UdonBehaviour[] type not in udon
    private object[][] behaviours;
    private string[][] methods;
    private int[][] futureCounts;
    // either -1 if one-shot event, or the discrete delay for the forever-repeating event
    // so we can keep resetting it forward.
    private int[][] repeats;
    private int[] bucketLengths = new int[WHEEL_SIZE];
    private int cursor = 0;

    void Start()
    {
        Init();
    }

    void Init()
    {
        if (initialized) return;
        behaviours = new object[WHEEL_SIZE][];
        methods = new string[WHEEL_SIZE][];
        futureCounts = new int[WHEEL_SIZE][];
        repeats = new int[WHEEL_SIZE][];
        for (int i = 0; i < WHEEL_SIZE; ++i)
        {
            behaviours[i] = new object[INITIAL_BUCKET_SIZE];
            methods[i] = new string[INITIAL_BUCKET_SIZE];
            futureCounts[i] = new int[INITIAL_BUCKET_SIZE];
            bucketLengths[i] = 0;
            repeats[i] = new int[INITIAL_BUCKET_SIZE];
        }
        initialized = true;
        //Debug.Log($"wheel initialized");
    }

    void Update()
    {
        int ticks = Mathf.FloorToInt(Time.deltaTime / WHEEL_RESOLUTION);
        //Debug.Log($"wheel ticking {ticks} forward");
        while (ticks-- > 0)
        {
            var c = cursor++;
            cursor %= WHEEL_SIZE;

            int bucketLen = bucketLengths[c];
            //Debug.Log($"wheel {c} is {bucketLen} size");
            if (bucketLen == 0) continue;

            object[] bucketUB = behaviours[c];
            string[] bucketM = methods[c];
            int[] bucketFC = futureCounts[c];
            int[] bucketR = repeats[c];
            for (int i = 0; i < bucketLen; ++i)
            {
                // decrement and skip any still future entries for next time around
                if (--bucketFC[i] > 0) continue;

                // actually send event
                var ub = (UdonBehaviour)bucketUB[i];
                var m = bucketM[i];
                //Debug.Log($"wheel sending {ub} {m} slot {c} bucket {i}");
                ub.SendCustomEvent(m);

                var repeat = bucketR[i];
                if (repeat >= 0)
                {
                    //Debug.Log($"rescheduling {ub} {m} slot {c} bucket {i} to {repeat}");
                    PushBucket(repeat, ub, m, true);
                }

                //Debug.Log($"removing {ub} {m} slot {c} bucket {i} repeat {repeat}");
                // remove entry from bucket (move all remaining entries back one)
                for (int j = i, k = i + 1; k < bucketLen; ++j, ++k)
                {
                    bucketUB[j] = bucketUB[k];
                    bucketM[j] = bucketM[k];
                    bucketFC[j] = bucketFC[k];
                    bucketR[j] = bucketR[k];
                }
                bucketLen = --bucketLengths[c]; // 1 less now
                i--; // rewind so next iteration will pick up new item (if present)
            }
        }
    }

    public void Delay(float delay, UdonBehaviour behaviour, string method)
    {
        Init(); // just in case another behavior tries to delay before wheel Start() called.
        PushBucket(Mathf.FloorToInt(delay / WHEEL_RESOLUTION), behaviour, method, false);
    }

    public void Repeat(float delay, UdonBehaviour behaviour, string method)
    {
        Init(); // just in case another behavior tries to delay before wheel Start() called.
        PushBucket(Mathf.FloorToInt(delay / WHEEL_RESOLUTION), behaviour, method, true);
    }

    private void PushBucket(int delayUnits, UdonBehaviour behaviour, string method, bool repeat)
    {
        int idx = (cursor + delayUnits) % WHEEL_SIZE;
        int futureCount = delayUnits / WHEEL_SIZE;

        object[] bucketUB = behaviours[idx];
        int bucketIdx = bucketLengths[idx]++;
        if (bucketIdx >= bucketUB.Length)
        {
            ExpandBucket(idx, bucketUB, bucketIdx);
        }

        string[] bucketM = methods[idx];
        int[] bucketFC = futureCounts[idx];
        int[] bucketR = repeats[idx];

        //Debug.Log($"adding {behaviour} {method} to slot {idx} bucket {bucketIdx} {repeat}");

        bucketUB[bucketIdx] = behaviour;
        bucketM[bucketIdx] = method;
        bucketFC[bucketIdx] = futureCount;
        bucketR[bucketIdx] = repeat ? delayUnits : -1;
    }

    private void ExpandBucket(int idx, object[] bucketUB, int len)
    {
        string[] bucketM = methods[idx];
        int[] bucketFC = futureCounts[idx];
        int[] bucketR = repeats[idx];
        var newLen = len + (len >> 1); // a la java arraylist
        object[] newBucketUB = new object[newLen];
        bucketUB.CopyTo(newBucketUB, 0);
        string[] newBucketM = new string[newLen];
        bucketM.CopyTo(newBucketM, 0);
        int[] newBucketFC = new int[newLen];
        bucketFC.CopyTo(newBucketFC, 0);
        int[] newBucketR = new int[newLen];
        bucketR.CopyTo(newBucketR, 0);

        behaviours[idx] = newBucketUB;
        methods[idx] = newBucketM;
        futureCounts[idx] = newBucketFC;
        repeats[idx] = newBucketR;
    }
}
