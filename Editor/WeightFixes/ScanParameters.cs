// ScanParameters.cs
//
// Plain-data view of the knobs that affect cross-side detection. Used as
// the boundary between the UI window (which serialises them as separate
// [SerializeField] fields) and the detector (which only needs values).
// Lives in the WhyKnot.AvatarQol.WeightFixes namespace so both the
// WeightSanityCheck UI and the runtime apply hook can pass the same
// shape around without duplicating field lists.

namespace WhyKnot.AvatarQol.WeightFixes {

    internal struct ScanParameters {

        public float WeightFloor;
        public float CenterMargin;
        public bool ScanCenterBand;
        public float CenterCrossSideFloor;

        public static ScanParameters Defaults => new ScanParameters {
            WeightFloor = 0f,
            CenterMargin = 0f,
            ScanCenterBand = false,
            CenterCrossSideFloor = 0.10f,
        };
    }
}
