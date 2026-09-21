#pragma once

#include "pch.h"
#include "Utf8String.h"
#include "FbxSdkModelPackExporter.h"
#include "FbxSdkBoneNameMapper.h"

using namespace System;
using namespace System::Collections::Generic;

namespace GFDLibrary::Conversion::FbxSdk
{
    using namespace Animations;
    using namespace Models;

    /// <summary>
    /// Adds baked GFD skeletal animation stacks to an FBX produced by FbxSdkModelPackExporter.
    /// The model exporter deliberately omits the synthetic identity model root, so the same node
    /// is skipped here. Every remaining model node receives baked local PRS curves at 30 fps.
    /// </summary>
    public ref class FbxSdkAnimationExporter abstract sealed
    {
    public:
        static void AppendFile(Model^ model, AnimationPack^ animationPack, String^ path)
        {
            AppendFile(model, animationPack, path, gcnew FbxSdkModelPackExporterConfig());
        }

        static void AppendFile(Model^ model, AnimationPack^ animationPack, String^ path,
            FbxSdkModelPackExporterConfig^ config)
        {
            if (model == nullptr)
                throw gcnew ArgumentNullException("model");
            if (animationPack == nullptr)
                throw gcnew ArgumentNullException("animationPack");
            if (String::IsNullOrWhiteSpace(path))
                throw gcnew ArgumentException("An FBX path is required.", "path");
            if (!System::IO::File::Exists(path))
                throw gcnew System::IO::FileNotFoundException("The base FBX does not exist.", path);
            if (animationPack->Animations == nullptr || animationPack->Animations->Count == 0)
                return;

            FbxManager* manager = FbxManager::Create();
            if (manager == nullptr)
                throw gcnew InvalidOperationException("Failed to create FBX manager for animation export.");

            FbxIOSettings* ioSettings = nullptr;
            FbxImporter* importer = nullptr;
            FbxScene* scene = nullptr;
            FbxExporter* exporter = nullptr;

            try
            {
                ioSettings = FbxIOSettings::Create(manager, IOSROOT);
                manager->SetIOSettings(ioSettings);

                importer = FbxImporter::Create(manager, "");
                if (!importer->Initialize(Utf8String(path).ToCStr(), -1, manager->GetIOSettings()))
                {
                    auto error = gcnew String(importer->GetStatus().GetErrorString());
                    throw gcnew InvalidOperationException("Failed to reopen FBX for animation export: " + error);
                }

                scene = FbxScene::Create(manager, "");
                if (scene == nullptr || !importer->Import(scene))
                {
                    auto error = importer == nullptr ? String::Empty : gcnew String(importer->GetStatus().GetErrorString());
                    throw gcnew InvalidOperationException("Failed to import base FBX for animation export: " + error);
                }
                importer->Destroy();
                importer = nullptr;

                scene->GetGlobalSettings().SetTimeMode(FbxTime::eFrames30);
                AddAnimations(scene, model, animationPack,
                    config != nullptr && config->UseUnrealBoneNames);

                exporter = FbxExporter::Create(manager, "");
                if (!exporter->SetFileExportVersion(FBX_2014_00_COMPATIBLE))
                    throw gcnew InvalidOperationException("Failed to set FBX animation export version.");
                if (!exporter->Initialize(Utf8String(path).ToCStr(), -1, manager->GetIOSettings()))
                {
                    auto error = gcnew String(exporter->GetStatus().GetErrorString());
                    throw gcnew InvalidOperationException("Failed to initialize animated FBX exporter: " + error);
                }
                if (!exporter->Export(scene))
                {
                    auto error = gcnew String(exporter->GetStatus().GetErrorString());
                    throw gcnew InvalidOperationException("Failed to write animated FBX: " + error);
                }
            }
            finally
            {
                if (exporter != nullptr)
                    exporter->Destroy();
                if (importer != nullptr)
                    importer->Destroy();
                if (scene != nullptr)
                    scene->Destroy();
                manager->Destroy();
            }
        }

    private:
        literal double FramesPerSecond = 30.0;

        static void AddAnimations(FbxScene* scene, Model^ model, AnimationPack^ animationPack,
            bool useUnrealBoneNames)
        {
            auto modelNodes = gcnew List<Node^>(model->Nodes);
            for (int animationIndex = 0; animationIndex < animationPack->Animations->Count; ++animationIndex)
            {
                auto animation = animationPack->Animations[animationIndex];
                if (animation == nullptr)
                    continue;

                auto stackName = String::Format("Animation_{0:D3}", animationIndex);
                auto stack = FbxAnimStack::Create(scene, Utf8String(stackName).ToCStr());
                auto layer = FbxAnimLayer::Create(scene, "BaseLayer");
                stack->AddMember(layer);

                FbxTime startTime;
                FbxTime endTime;
                startTime.SetSecondDouble(0.0);
                endTime.SetSecondDouble(Math::Max(0.0, (double)animation->Duration));
                stack->SetLocalTimeSpan(FbxTimeSpan(startTime, endTime));

                auto sampler = gcnew AnimationLocalPoseSampler(model, animation);
                auto fbxNodes = gcnew array<IntPtr>(modelNodes->Count);
                for (int nodeIndex = 0; nodeIndex < modelNodes->Count; ++nodeIndex)
                {
                    auto node = modelNodes[nodeIndex];
                    if (Object::ReferenceEquals(node, model->RootNode))
                        continue;
                    auto exportName = FbxSdkBoneNameMapper::GetExportName(
                        model, node, useUnrealBoneNames);
                    auto fbxNode = scene->GetRootNode()->FindChild(Utf8String(exportName).ToCStr(), true);
                    if (fbxNode != nullptr)
                        fbxNodes[nodeIndex] = IntPtr(fbxNode);
                }

                auto previousEuler = gcnew array<System::Numerics::Vector3>(modelNodes->Count);
                auto hasPreviousEuler = gcnew array<bool>(modelNodes->Count);
                auto duration = Math::Max(0.0, (double)animation->Duration);
                auto lastFrame = Math::Max(0, (int)Math::Ceiling(duration * FramesPerSecond));

                for (int frame = 0; frame <= lastFrame; ++frame)
                {
                    auto seconds = Math::Min(duration, frame / FramesPerSecond);
                    auto transforms = sampler->Evaluate((float)seconds);
                    FbxTime keyTime;
                    keyTime.SetSecondDouble(seconds);

                    for (int nodeIndex = 0; nodeIndex < modelNodes->Count; ++nodeIndex)
                    {
                        if (fbxNodes[nodeIndex] == IntPtr::Zero)
                            continue;

                        auto fbxNode = static_cast<FbxNode*>(fbxNodes[nodeIndex].ToPointer());
                        auto transform = transforms[nodeIndex];
                        auto translation = transform.Translation;
                        auto scale = transform.Scale;
                        auto rotation = transform.Rotation;

                        FbxAMatrix rotationMatrix;
                        rotationMatrix.SetQ(FbxQuaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
                        auto rawEuler = rotationMatrix.GetR();
                        auto euler = System::Numerics::Vector3(
                            (float)rawEuler[0], (float)rawEuler[1], (float)rawEuler[2]);
                        if (hasPreviousEuler[nodeIndex])
                        {
                            auto previous = previousEuler[nodeIndex];
                            euler.X = (float)UnwrapAngle(euler.X, previous.X);
                            euler.Y = (float)UnwrapAngle(euler.Y, previous.Y);
                            euler.Z = (float)UnwrapAngle(euler.Z, previous.Z);
                        }
                        previousEuler[nodeIndex] = euler;
                        hasPreviousEuler[nodeIndex] = true;

                        AddVectorKey(fbxNode->LclTranslation, layer, keyTime,
                            translation.X, translation.Y, translation.Z);
                        AddVectorKey(fbxNode->LclRotation, layer, keyTime,
                            euler.X, euler.Y, euler.Z);
                        AddVectorKey(fbxNode->LclScaling, layer, keyTime,
                            scale.X, scale.Y, scale.Z);
                    }
                }
            }
        }

        static void AddVectorKey(FbxPropertyT<FbxDouble3>& property, FbxAnimLayer* layer, FbxTime& time,
            double x, double y, double z)
        {
            AddCurveKey(property.GetCurve(layer, FBXSDK_CURVENODE_COMPONENT_X, true), time, x);
            AddCurveKey(property.GetCurve(layer, FBXSDK_CURVENODE_COMPONENT_Y, true), time, y);
            AddCurveKey(property.GetCurve(layer, FBXSDK_CURVENODE_COMPONENT_Z, true), time, z);
        }

        static void AddCurveKey(FbxAnimCurve* curve, FbxTime& time, double value)
        {
            if (curve == nullptr)
                return;
            auto keyIndex = curve->KeyAdd(time);
            curve->KeySetValue(keyIndex, (float)value);
            curve->KeySetInterpolation(keyIndex, FbxAnimCurveDef::eInterpolationLinear);
        }

        static double UnwrapAngle(double value, double previous)
        {
            while (value - previous > 180.0)
                value -= 360.0;
            while (value - previous < -180.0)
                value += 360.0;
            return value;
        }
    };
}
