using UnityEngine;
using VRC.SDKBase;

namespace ContactFix
{
    // IEditorOnly => SDK validator ignores it AND it's stripped before upload,
    // so it never lands on the final avatar.
    [DisallowMultipleComponent]
    public class ContactFixMerger : MonoBehaviour, IEditorOnly
    {
        [Tooltip("Source controller. Its 'aap convert' blend tree child is duplicated " +
                 "once per detected SPS plug contact instance and kept inside the same direct blend tree.\n\n" +
                 "Supported markers:\n" +
                 "  vf??_      one-to-one VRCFury parameter marker\n" +
                 "  repath_/   animation binding object-path marker\n" +
                 "  rescan_    material-property curve expansion marker")]
        public RuntimeAnimatorController sourceController;

        [Tooltip("When an animated AAP parameter starts with VF##_ and ends with /Length, keys at 0.3 are changed to this value.")]
        public float lengthAAPValue = 0.14f;

        [Tooltip("Show a popup report after preprocessing with successful actions, skipped work, and failures. Leave this off for faster uploads.")]
        public bool debugReport;
    }
}
