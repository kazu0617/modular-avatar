using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.animation;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using EditorCurveBinding = UnityEditor.EditorCurveBinding;
using Object = UnityEngine.Object;

namespace nadena.dev.modular_avatar.core.editor
{
    /// <summary>
    /// The class to retarget m_IsActive animation to moved multiple objects
    /// </summary>
    internal class ActiveAnimationRetargeter
    {
        private readonly BuildContext _context;
        private readonly BoneDatabase _boneDatabase;
        private readonly AnimatorServicesContext _asc;
        private readonly List<IntermediateObj> _intermediateObjs = new List<IntermediateObj>();

        /// <summary>
        /// Tracks an object whose Active state is animated, and which leads up to this Merge Animator component.
        /// We use this tracking data to create proxy objects within the main armature, which track the same active
        /// state.
        /// </summary>
        struct IntermediateObj
        {
            /// <summary>
            /// The path of original object
            /// </summary>
            public string OriginalPath;

            /// <summary>
            /// Name of the intermediate object. Used to name proxy objects.
            /// </summary>
            public string Name;

            /// <summary>
            /// Whether this object is initially active.
            /// </summary>
            public bool InitiallyActive;

            /// <summary>
            /// List of created Intermediate Objects
            /// </summary>
            public List<GameObject> Created;
        }

        public ActiveAnimationRetargeter(
            BuildContext context,
            BoneDatabase boneDatabase,
            Transform root
        )
        {
            _context = context;
            _boneDatabase = boneDatabase;
            _asc = context.PluginBuildContext.Extension<AnimatorServicesContext>();

            while (root != null && !RuntimeUtil.IsAvatarRoot(root))
            {
                var originalPath = RuntimeUtil.AvatarRootPath(root.gameObject);
                System.Diagnostics.Debug.Assert(originalPath != null);

                if (_asc.AnimationIndex.GetClipsForObjectPath(originalPath).Any(clip =>
                        GetActiveBinding(clip, originalPath) != null
                    ))
                {
                    _intermediateObjs.Add(new IntermediateObj
                    {
                        OriginalPath = originalPath,
                        Name = $"{root.gameObject.name}${Guid.NewGuid()}",
                        InitiallyActive = root.gameObject.activeSelf,
                        Created = new List<GameObject>(),
                    });
                }

                root = root.parent;
            }

            // currently _intermediateObjs is in child -> parent order.
            // we want parent -> child order so reverse entire list
            _intermediateObjs.Reverse();
        }

        public IEnumerable<GameObject> AddedGameObjects => _intermediateObjs.SelectMany(x => x.Created);

        public GameObject CreateIntermediateObjects(GameObject sourceBone)
        {
            for (var i = 0; i < _intermediateObjs.Count; i++)
            {
                var intermediate = _intermediateObjs[i];
                var preexisting = sourceBone.transform.Find(intermediate.Name);
                if (preexisting != null)
                {
                    sourceBone = preexisting.gameObject;
                    continue;
                }

                var switchObj = new GameObject(intermediate.Name);
                switchObj.transform.SetParent(sourceBone.transform, false);
                switchObj.transform.localPosition = Vector3.zero;
                switchObj.transform.localRotation = Quaternion.identity;
                switchObj.transform.localScale = Vector3.one;
                switchObj.SetActive(intermediate.InitiallyActive);

                if (i == 0)
                {
                    // This new leaf can break parent bone physbones. Add a PB Blocker
                    // to prevent this becoming an issue.
                    switchObj.GetOrAddComponent<ModularAvatarPBBlocker>();
                }

                intermediate.Created.Add(switchObj);

                sourceBone = switchObj;

                // Ensure mesh retargeting looks through this 
                _boneDatabase.AddMergedBone(sourceBone.transform);
                _boneDatabase.RetainMergedBone(sourceBone.transform);
            }

            return sourceBone;
        }

        public void FixupAnimations()
        {
            foreach (var intermediate in _intermediateObjs)
            {
                var path = intermediate.OriginalPath;

                foreach (var clip in _asc.AnimationIndex.GetClipsForObjectPath(path))
                {
                    var curve = GetActiveBinding(clip, path);
                    if (curve != null)
                    {
                        foreach (var mapping in intermediate.Created)
                        {
                            clip.SetFloatCurve(_asc.ObjectPathRemapper.GetVirtualPathForObject(mapping), typeof(GameObject), "m_IsActive",
                                curve);
                        }
                    }
                }
            }
        }

        private AnimationCurve GetActiveBinding(VirtualClip clip, string path)
        {
            return clip.GetFloatCurve(EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"));
        }
    }
}