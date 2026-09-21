#pragma once

#include "pch.h"

using namespace System;

namespace GFDLibrary::Conversion::FbxSdk
{
    using namespace Animations;
    using namespace Models;

    /// <summary>
    /// Maps the semantic humanoid roles already used by AniMatch/retargeting to
    /// the standard Unreal mannequin bone names. This deliberately does not key
    /// off raw P5/P5R/P5D names: those rigs use different naming conventions.
    /// Unrecognized helper, cloth, face and hair nodes keep their original names.
    /// </summary>
    public ref class FbxSdkBoneNameMapper abstract sealed
    {
    public:
        static String^ GetExportName(Model^ model, Node^ node, bool useUnrealBoneNames)
        {
            if (node == nullptr || String::IsNullOrWhiteSpace(node->Name) || !useUnrealBoneNames)
                return node == nullptr ? String::Empty : node->Name;

            auto role = AnimationSkeletonRoles::GetRole(node->Name);
            if (String::IsNullOrWhiteSpace(role))
                return node->Name;

            // P5-style rigs can contain both a coordinate/helper node named
            // "root" and a dedicated motion root (Bip01). Avoid producing two
            // FBX nodes named "root": Unreal's root name belongs to the motion
            // root, while the coordinate helper keeps a unique GFD-only name.
            if (String::Equals(role, AnimationSkeletonRoles::MotionRootRole,
                               StringComparison::OrdinalIgnoreCase))
                return "root";
            if (String::Equals(role, AnimationSkeletonRoles::RootRole,
                               StringComparison::OrdinalIgnoreCase))
                return HasDedicatedMotionRoot(model) ? "gfd_axis_root" : "root";

            if (EqualsRole(role, "hips"))   return "pelvis";
            if (EqualsRole(role, "spine"))  return "spine_01";
            if (EqualsRole(role, "spine1")) return "spine_02";
            if (EqualsRole(role, "spine2")) return "spine_03";
            if (EqualsRole(role, "neck"))   return "neck_01";
            if (EqualsRole(role, "head"))   return "head";

            String^ sideSuffix = nullptr;
            String^ limbRole = nullptr;
            if (role->StartsWith("left", StringComparison::OrdinalIgnoreCase))
            {
                sideSuffix = "_l";
                limbRole = role->Substring(4);
            }
            else if (role->StartsWith("right", StringComparison::OrdinalIgnoreCase))
            {
                sideSuffix = "_r";
                limbRole = role->Substring(5);
            }

            if (sideSuffix == nullptr || String::IsNullOrEmpty(limbRole))
                return node->Name;

            if (EqualsRole(limbRole, "shoulder")) return "clavicle" + sideSuffix;
            if (EqualsRole(limbRole, "arm"))      return "upperarm" + sideSuffix;
            if (EqualsRole(limbRole, "forearm"))  return "lowerarm" + sideSuffix;
            if (EqualsRole(limbRole, "hand"))     return "hand" + sideSuffix;
            if (EqualsRole(limbRole, "upleg"))    return "thigh" + sideSuffix;
            if (EqualsRole(limbRole, "leg"))      return "calf" + sideSuffix;
            if (EqualsRole(limbRole, "foot"))     return "foot" + sideSuffix;
            if (EqualsRole(limbRole, "toe"))      return "ball" + sideSuffix;

            auto finger = MapFinger(limbRole, sideSuffix);
            return finger != nullptr ? finger : node->Name;
        }

    private:
        static bool EqualsRole(String^ value, String^ expected)
        {
            return String::Equals(value, expected, StringComparison::OrdinalIgnoreCase);
        }

        static bool HasDedicatedMotionRoot(Model^ model)
        {
            if (model == nullptr)
                return false;

            for each (auto candidate in model->Nodes)
            {
                auto candidateRole = AnimationSkeletonRoles::GetRole(candidate->Name);
                if (String::Equals(candidateRole, AnimationSkeletonRoles::MotionRootRole,
                                   StringComparison::OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static String^ MapFinger(String^ limbRole, String^ sideSuffix)
        {
            array<String^>^ names = { "thumb", "index", "middle", "ring", "pinky" };
            for each (auto name in names)
            {
                if (!limbRole->StartsWith(name, StringComparison::OrdinalIgnoreCase))
                    continue;

                auto segmentText = limbRole->Substring(name->Length);
                int segment = 0;
                if (!Int32::TryParse(segmentText, segment) || segment < 1 || segment > 3)
                    return nullptr;

                return String::Format("{0}_{1:D2}{2}", name, segment, sideSuffix);
            }

            return nullptr;
        }
    };
}
