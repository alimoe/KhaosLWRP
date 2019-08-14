﻿using System;
using System.Collections.Generic;
using UnityEngine.Rendering;


namespace UnityEngine.Experimental.Rendering.LightweightPipeline
{
    public class MainCharacterShadowCasterPass : ScriptableRenderPass
    {
        const int k_ShadowmapBufferBits = 16;
        RenderTexture m_MainCharacterShadowmapTexture;
        RenderTextureFormat m_ShadowmapFormat;

        Matrix4x4 m_ViewMatrix;
        Matrix4x4 m_ProjMatrix;
        Matrix4x4 m_MainCharacterShadowMatrix;

        const string k_RenderMainCharacterShadowmapTag = "Render Main Character Shadowmap";

        private RenderTargetHandle destination { get; set; }

        private List<Renderer> _Renderers = new List<Renderer>();

        public MainCharacterShadowCasterPass()
        {
            RegisterShaderPassName("ShadowCaster");

            m_ShadowmapFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Shadowmap)
                ? RenderTextureFormat.Shadowmap
                : RenderTextureFormat.Depth;
        }

        public bool Setup(RenderTargetHandle destination, ref RenderingData renderingData)
        {
            this.destination = destination;
            int shadowLightIndex = renderingData.lightData.mainLightIndex;
            if (-1 == shadowLightIndex)
            {
                return false;
            }

            VisibleLight shadowLight = renderingData.lightData.visibleLights[shadowLightIndex];
            Light light = shadowLight.light;
            if (LightShadows.None == light.shadows)
            {
                return false;
            }

            if (LightType.Directional != shadowLight.lightType)
            {
                Debug.LogWarning("Only directional lights are supported as main light.");
            }

            return true;
        }

        bool GetVPMatrix(Light light)
        {
            GameObject[] players = GameObject.FindGameObjectsWithTag("Player");
            if (null == players || 0 == players.Length)
            {
                return false;
            }
            _Renderers.Clear();
            Bounds bounds = new Bounds();
            foreach (var p in players)
            {
                Renderer r = p.GetComponent<Renderer>();
                if (null != r && r.enabled && r.shadowCastingMode != ShadowCastingMode.Off)
                {
                    if (r is SkinnedMeshRenderer)
                    {
                        (r as SkinnedMeshRenderer).sharedMesh.RecalculateBounds();
                    }
                    Bounds rb = r.bounds;
                    if (0 != _Renderers.Count)
                    {
                        bounds.Encapsulate(rb);
                    }
                    else
                    {
                        bounds = rb;
                    }
                    _Renderers.Add(r);
                }
            }
            if (0 == _Renderers.Count)
            {
                return false;
            }

            Vector3 axisX = light.transform.right.normalized;
            Vector3 axisY = light.transform.up.normalized;
            Vector3 axisZ = light.transform.worldToLocalMatrix.GetColumn(2).normalized;

            Vector3 center = bounds.center;
            float radius = bounds.extents.magnitude;
            Vector3 intialLightPos = center;// - light.transform.forward.normalized * radius;

            Matrix4x4 projMatrix = Matrix4x4.Ortho(-radius, radius, -radius, radius, radius * 0.1f, radius * 2.3f);
            Matrix4x4 viewMatrix = light.transform.worldToLocalMatrix;
            viewMatrix.SetColumn(2, -viewMatrix.GetColumn(2));
            Vector4 viewTsl = -viewMatrix.MultiplyVector(intialLightPos);
            viewTsl.z -= radius * 1.2f;
            viewTsl.w = 1;
            viewMatrix.SetColumn(3, viewTsl);
            m_MainCharacterShadowMatrix = ShadowUtils.GetShadowTransform(projMatrix, viewMatrix);
            m_ViewMatrix = viewMatrix;
            m_ProjMatrix = projMatrix;

            return true;
        }
        /// <inheritdoc/>
        public override void FrameCleanup(CommandBuffer cmd)
        {
            if (cmd == null)
                throw new ArgumentNullException("cmd");
            cmd.DisableShaderKeyword("_MAIN_CHARACTER_SHADOWS");

            if (m_MainCharacterShadowmapTexture)
            {
                RenderTexture.ReleaseTemporary(m_MainCharacterShadowmapTexture);
                m_MainCharacterShadowmapTexture = null;
            }
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderer renderer, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (renderer == null)
                throw new ArgumentNullException("renderer");

            if (!renderingData.shadowData.supportsMainLightShadows)
            {
                return;
            }
            LightData lightData = renderingData.lightData;
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex == -1)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            ShadowData shadowData = renderingData.shadowData;

            if (!GetVPMatrix(shadowLight.light))
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get(k_RenderMainCharacterShadowmapTag);
            using (new ProfilingSample(cmd, k_RenderMainCharacterShadowmapTag))
            {
                m_MainCharacterShadowmapTexture = RenderTexture.GetTemporary(shadowData.mainLightShadowmapWidth,
                    shadowData.mainLightShadowmapHeight, k_ShadowmapBufferBits, m_ShadowmapFormat);
                m_MainCharacterShadowmapTexture.filterMode = FilterMode.Bilinear;
                m_MainCharacterShadowmapTexture.wrapMode = TextureWrapMode.Clamp;
                m_MainCharacterShadowmapTexture.name = "m_MainCharacterShadowmapTexture";
                SetRenderTarget(cmd, m_MainCharacterShadowmapTexture, RenderBufferLoadAction.DontCare,
                    RenderBufferStoreAction.Store, ClearFlag.Depth, Color.black, TextureDimension.Tex2D);
                
                Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, shadowLightIndex, ref shadowData, m_ProjMatrix, shadowData.mainLightShadowmapWidth);
                ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref shadowLight, shadowBias);

                cmd.SetViewProjectionMatrices(m_ViewMatrix, m_ProjMatrix);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                foreach(var r in _Renderers)
                {
                    cmd.DrawRenderer(r, r.sharedMaterial, 0, r.sharedMaterial.FindPass("ShadowCaster"));
                    r.sharedMaterial.EnableKeyword("_MAIN_CHARACTER_SHADOWS");
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                SetupMainCharacterShadowReceiverConstants(cmd, ref shadowData, shadowLight);
            }

            CoreUtils.SetKeyword(cmd, "_MAIN_CHARACTER_SHADOWS_TEXTURE", true);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SoftShadows, shadowLight.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void SetupMainCharacterShadowReceiverConstants(CommandBuffer cmd, ref ShadowData shadowData, VisibleLight shadowLight)
        {
            Light light = shadowLight.light;

            //int cascadeCount = m_ShadowCasterCascadesCount;
            //for (int i = 0; i < k_MaxCascades; ++i)
            //    m_MainLightShadowMatrices[i] = (cascadeCount >= i) ? m_CascadeSlices[i].shadowTransform : Matrix4x4.identity;

            //// We setup and additional a no-op WorldToShadow matrix in the last index
            //// because the ComputeCascadeIndex function in Shadows.hlsl can return an index
            //// out of bounds. (position not inside any cascade) and we want to avoid branching
            //Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
            //noOpShadowMatrix.m33 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
            //m_MainLightShadowMatrices[k_MaxCascades] = noOpShadowMatrix;

            float invShadowAtlasWidth = 1.0f / shadowData.mainLightShadowmapWidth;
            float invShadowAtlasHeight = 1.0f / shadowData.mainLightShadowmapHeight;
            float invHalfShadowAtlasWidth = 0.5f * invShadowAtlasWidth;
            float invHalfShadowAtlasHeight = 0.5f * invShadowAtlasHeight;
            cmd.SetGlobalTexture(destination.id, m_MainCharacterShadowmapTexture);
            cmd.SetGlobalMatrix("_MainCharacterWorldToShadow", m_MainCharacterShadowMatrix);
            cmd.SetGlobalFloat("_MainCharacterShadowStrength", light.shadowStrength);
            cmd.SetGlobalVector("_MainCharacterShadowmapSize", new Vector4(invShadowAtlasWidth, invShadowAtlasHeight,
                shadowData.mainLightShadowmapWidth, shadowData.mainLightShadowmapHeight));
            cmd.SetGlobalVector("_MainCharacterShadowOffset0", new Vector4(-invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight, 0.0f, 0.0f));
            cmd.SetGlobalVector("_MainCharacterShadowOffset1", new Vector4(invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight, 0.0f, 0.0f));
            cmd.SetGlobalVector("_MainCharacterShadowOffset2", new Vector4(-invHalfShadowAtlasWidth, invHalfShadowAtlasHeight, 0.0f, 0.0f));
            cmd.SetGlobalVector("_MainCharacterShadowOffset3", new Vector4(invHalfShadowAtlasWidth, invHalfShadowAtlasHeight, 0.0f, 0.0f));

        }
    }
}