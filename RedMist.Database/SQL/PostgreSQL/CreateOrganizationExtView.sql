-- PostgreSQL View Creation Script
-- This script creates the OrganizationExtView in PostgreSQL
-- Run this after applying the initial migration

-- Create the view
CREATE OR REPLACE VIEW public."OrganizationExtView" AS
SELECT 
    o."Id",
    o."ClientId",
    o."ControlLogParams",
    o."ControlLogType",
  COALESCE(o."Logo", d."ImageData") AS "Logo",
    o."Name",
    o."Orbits",
    o."ShortName",
    o."Website",
    o."X2",
    o."MultiloopIp",
  o."MultiloopPort",
    o."OrbitsLogsPath",
    o."RMonitorIp",
    o."RMonitorPort"
FROM public."Organizations" o
CROSS JOIN (
    SELECT "ImageData" 
    FROM public."DefaultOrgImages" 
    LIMIT 1
) d;

-- Grant permissions (adjust as needed for your security requirements)
-- GRANT SELECT ON public."OrganizationExtView" TO your_app_user;
