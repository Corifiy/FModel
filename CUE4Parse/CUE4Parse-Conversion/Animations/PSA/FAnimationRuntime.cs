using System;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;

namespace CUE4Parse_Conversion.Animations.PSA
{
    public static class FAnimationRuntime
    {
        public static FCompactPose[] LoadRestAsPoses(USkeleton skeleton)
        {
            var poses = new FCompactPose[1];
            for (int frameIndex = 0; frameIndex < poses.Length; frameIndex++)
            {
                poses[frameIndex] = new FCompactPose(skeleton.BoneCount);
                for (var boneIndex = 0; boneIndex < poses[frameIndex].Bones.Length; boneIndex++)
                {
                    var boneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo[boneIndex];
                    poses[frameIndex].Bones[boneIndex] = new FPoseBone
                    {
                        Name = boneInfo.Name.ToString(),
                        ParentIndex = boneInfo.ParentIndex,
                        Transform = (FTransform)skeleton.ReferenceSkeleton.FinalRefBonePose[boneIndex].Clone(),
                        IsValidKey = true
                    };
                }
            }
            return poses;
        }

        public static FCompactPose[] LoadAsPoses(CAnimSequence sequence, USkeleton skeleton, int refFrame)
        {
            // RefFrameIndex is authored against the source animation and can point past the last
            // sampled key of the compressed data, UE clamps it the same way when building a base pose
            refFrame = Math.Clamp(refFrame, 0, Math.Max(0, sequence.NumFrames - 1));

            var poses = new FCompactPose[1];
            for (int frameIndex = 0; frameIndex < poses.Length; frameIndex++)
            {
                poses[frameIndex] = new FCompactPose(skeleton.BoneCount);
                for (var boneIndex = 0; boneIndex < poses[frameIndex].Bones.Length; boneIndex++)
                {
                    var boneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo[boneIndex];
                    var originalTransform = skeleton.ReferenceSkeleton.FinalRefBonePose[boneIndex];
                    var track = sequence.Tracks[boneIndex];

                    var boneOrientation = FQuat.Identity;
                    var bonePosition = FVector.ZeroVector;
                    var boneScale = FVector.OneVector;

                    track.GetBoneTransform(refFrame, sequence.NumFrames, ref boneOrientation, ref bonePosition, ref boneScale);

                    switch (skeleton.BoneTree[boneIndex])
                    {
                        case EBoneTranslationRetargetingMode.Skeleton:
                        {
                            var targetTransform = sequence.RetargetBasePose?[boneIndex] ?? originalTransform;
                            bonePosition = targetTransform.Translation;
                            break;
                        }
                        case EBoneTranslationRetargetingMode.AnimationScaled:
                        {
                            var sourceTranslationLength = originalTransform.Translation.Size();
                            if (sourceTranslationLength > UnrealMath.KindaSmallNumber)
                            {
                                var targetTranslationLength = sequence.RetargetBasePose?[boneIndex].Translation.Size() ?? sourceTranslationLength;
                                bonePosition.Scale(targetTranslationLength / sourceTranslationLength);
                            }
                            break;
                        }
                        case EBoneTranslationRetargetingMode.AnimationRelative:
                        {
                            // can't tell if it's working or not
                            var sourceSkelTrans = originalTransform.Translation;
                            var refPoseTransform  = sequence.RetargetBasePose?[boneIndex] ?? originalTransform;

                            boneOrientation = boneOrientation * FQuat.Conjugate(originalTransform.Rotation) * refPoseTransform.Rotation;
                            bonePosition += refPoseTransform.Translation - sourceSkelTrans;
                            boneScale *= refPoseTransform.Scale3D * originalTransform.Scale3D;
                            boneOrientation.Normalize();
                            break;
                        }
                        case EBoneTranslationRetargetingMode.OrientAndScale:
                        {
                            var sourceSkelTrans = originalTransform.Translation;
                            var targetSkelTrans = sequence.RetargetBasePose?[boneIndex].Translation ?? sourceSkelTrans;

                            if (!sourceSkelTrans.Equals(targetSkelTrans))
                            {
                                var sourceSkelTransLength = sourceSkelTrans.Size();
                                var targetSkelTransLength = targetSkelTrans.Size();
                                if (!UnrealMath.IsNearlyZero(sourceSkelTransLength * targetSkelTransLength))
                                {
                                    var sourceSkelTransDir = sourceSkelTrans / sourceSkelTransLength;
                                    var targetSkelTransDir = targetSkelTrans / targetSkelTransLength;

                                    var deltaRotation = FQuat.FindBetweenNormals(sourceSkelTransDir, targetSkelTransDir);
                                    var scale = targetSkelTransLength / sourceSkelTransLength;
                                    bonePosition = deltaRotation.RotateVector(bonePosition) * scale;
                                }
                            }
                            break;
                        }
                    }

                    poses[frameIndex].Bones[boneIndex] = new FPoseBone
                    {
                        Name = boneInfo.Name.ToString(),
                        ParentIndex = boneInfo.ParentIndex,
                        Transform = new FTransform(boneOrientation, bonePosition, boneScale),
                        IsValidKey = true
                    };
                }
            }
            return poses;
        }

        public static FCompactPose[] LoadAsPoses(CAnimSequence sequence, USkeleton skeleton)
        {
            // a track without scale keys keeps whatever we seed it with, and the neutral value
            // differs between an additive delta (0) and an absolute pose (1)
            var defaultScale = sequence.IsAdditive ? FVector.ZeroVector : FVector.OneVector;

            var poses = new FCompactPose[sequence.NumFrames];
            for (int frameIndex = 0; frameIndex < poses.Length; frameIndex++)
            {
                poses[frameIndex] = new FCompactPose(skeleton.BoneCount);
                for (var boneIndex = 0; boneIndex < poses[frameIndex].Bones.Length; boneIndex++)
                {
                    var boneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo[boneIndex];
                    var track = sequence.Tracks[boneIndex];

                    var boneOrientation = FQuat.Identity;
                    var bonePosition = FVector.ZeroVector;
                    var boneScale = defaultScale;

                    track.GetBoneTransform(frameIndex, sequence.NumFrames, ref boneOrientation, ref bonePosition, ref boneScale);

                    poses[frameIndex].Bones[boneIndex] = new FPoseBone
                    {
                        Name = boneInfo.Name.ToString(),
                        ParentIndex = boneInfo.ParentIndex,
                        Transform = new FTransform(boneOrientation, bonePosition, boneScale),
                        // a constant track holds a single key that stays valid for every frame,
                        // comparing the frame against the key count drops it after the second one
                        IsValidKey = track.HasKeys()
                    };
                }
            }
            return poses;
        }

        public static void AccumulateLocalSpaceAdditivePoseInternal(FCompactPose basePose, FCompactPose additivePose, float weight)
        {
            if (weight < 0.999989986419678)
                throw new NotImplementedException();

            for (int index = 0; index < basePose.Bones.Length; index++)
            {
                basePose.Bones[index].AccumulateWithAdditiveScale(additivePose.Bones[index].Transform, weight);
            }
        }

        public static void AccumulateMeshSpaceRotationAdditiveToLocalPoseInternal(FCompactPose basePose, FCompactPose additivePose, float weight)
        {
            ConvertPoseToMeshRotation(basePose);
            AccumulateLocalSpaceAdditivePoseInternal(basePose, additivePose, weight);
            ConvertMeshRotationPoseToLocalSpace(basePose);
        }

        public static void ConvertPoseToMeshRotation(FCompactPose localPose)
        {
            for (var boneIndex = 1; boneIndex < localPose.Bones.Length; ++boneIndex)
            {
                var parentIndex = localPose.Bones[boneIndex].ParentIndex;
                var meshSpaceRotation = localPose.Bones[parentIndex].Transform.Rotation * localPose.Bones[boneIndex].Transform.Rotation;
                localPose.Bones[boneIndex].Transform.Rotation = meshSpaceRotation;
            }
        }

        public static void ConvertMeshRotationPoseToLocalSpace(FCompactPose pose)
        {
            for (var boneIndex = pose.Bones.Length - 1; boneIndex > 0; --boneIndex)
            {
                var parentIndex = pose.Bones[boneIndex].ParentIndex;
                var localSpaceRotation = pose.Bones[parentIndex].Transform.Rotation.Inverse() * pose.Bones[boneIndex].Transform.Rotation;
                pose.Bones[boneIndex].Transform.Rotation = localSpaceRotation;
            }
        }
    }
}
